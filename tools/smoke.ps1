# Hummingbird Airlines end-to-end smoke test.
# Usage: ./tools/smoke.ps1 [-BaseUrl http://localhost:5000] [-SkipEvictionTest]
# Requires a freshly started app (state is in memory only).

param(
    [string]$BaseUrl = "http://localhost:5000",
    [switch]$SkipEvictionTest
)

$script:pass = 0
$script:fail = 0

function Section([string]$title) {
    ""
    "=== $title ==="
}

function Invoke-Api {
    param([string]$Method, [string]$Uri, [string]$JsonBody = "", [hashtable]$Headers = @{})
    try {
        $params = @{
            Method             = $Method
            Uri                = $Uri
            Headers            = $Headers
            ContentType        = "application/json"
            TimeoutSec         = 90
            SkipHttpErrorCheck = $true
        }
        if ($JsonBody -ne "") {
            $params.Body = $JsonBody
        }

        $response = Invoke-WebRequest @params
        $content = if ($response.Content -is [byte[]]) {
            [System.Text.Encoding]::UTF8.GetString($response.Content)
        }
        else {
            [string]$response.Content
        }

        return [pscustomobject]@{
            StatusCode = [int]$response.StatusCode
            Content    = $content
        }
    }
    catch {
        Write-Host "REQUEST FAILED: $($_.Exception.Message)" -ForegroundColor Red
        return $null
    }
}

function Show-Result {
    param($Response, [int]$ExpectStatus, [string]$ExpectCode = "")
    if ($null -eq $Response) {
        $script:fail++
        Write-Host "FAIL (no response)" -ForegroundColor Red
        return
    }

    $statusOk = $Response.StatusCode -eq $ExpectStatus
    $codeOk = $true
    $actualCode = ""
    if ($ExpectCode) {
        $actualCode = ($Response.Content | ConvertFrom-Json).code
        $codeOk = ($actualCode -eq $ExpectCode)
    }

    if ($statusOk -and $codeOk) {
        $script:pass++
        $label = "$($Response.StatusCode)"
        if ($ExpectCode) { $label += " [$ExpectCode]" }
        Write-Host "PASS $label" -ForegroundColor Green
        $preview = $Response.Content
        if ($preview -and $preview.Length -gt 500) { $preview = $preview.Substring(0, 500) + " ..." }
        if ($preview) { Write-Host "     $preview" -ForegroundColor DarkGray }
    }
    else {
        $script:fail++
        Write-Host "FAIL got $($Response.StatusCode)$(if($actualCode){" [$actualCode]"}) expected $ExpectStatus $(if($ExpectCode){"/ $ExpectCode"})" -ForegroundColor Red
        Write-Host $Response.Content -ForegroundColor DarkGray
    }
}

# ---------------------------------------------------------------------------
Section "Health: flights search PEK->CDG (REST -> SOAP round-trip)"
Show-Result (Invoke-Api Get "$BaseUrl/api/v1/flights?from=PEK&to=CDG") 200

Section "Flight detail HB900 (~29 min before departure => check-in closed soon)"
Show-Result (Invoke-Api Get "$BaseUrl/api/v1/flights/HB900") 200

# ---------------------------------------------------------------------------
Section "Create booking on a future flight"
$allFlights = @((Invoke-RestMethod "$BaseUrl/api/v1/flights"))
$flightNumber = ($allFlights |
    Where-Object { $_.status -eq 'scheduled' -and -not $_.isCancelled } |
    Sort-Object scheduledDepartureUtc |
    Select-Object -First 1).flightNumber
if (-not $flightNumber) {
    Write-Host "FAIL no scheduled flight found for the test" -ForegroundColor Red
    exit 1
}
$carrier = $flightNumber.Substring(0,2).ToLower()
$number = [int]$flightNumber.Substring(2)
"Using flight $flightNumber (carrier=$carrier number=$number)"

$created = Invoke-Api Post "$BaseUrl/api/v1/bookings" @"
{"flight":{"carrier":"$carrier","number":$number},"cabinClass":"economy","passenger":{"firstName":"Smoke","lastName":"Tester","passport":"SMOKE01"}}
"@
Show-Result $created 201
$bookingRef = ($created.Content | ConvertFrom-Json).bookingRef
"Created booking $bookingRef"
if (-not $bookingRef) {
    Write-Host "FAIL no booking ref created; aborting dependent tests" -ForegroundColor Red
    exit 1
}

Section "GET booking by reference (nested designator)"
$bookingGet = Invoke-Api Get "$BaseUrl/api/v1/bookings/$bookingRef"
Show-Result $bookingGet 200
$bg = $bookingGet.Content | ConvertFrom-Json
if ($bg.flight.carrier -and $bg.flight.number) {
    $script:pass++; Write-Host "PASS booking contains nested flight designator ($($bg.flight.carrier):$($bg.flight.number))" -ForegroundColor Green
} else { $script:fail++; Write-Host "FAIL booking missing flight designator" -ForegroundColor Red }

Section "PUT update cabin class"
Show-Result (Invoke-Api Put "$BaseUrl/api/v1/bookings/$bookingRef" @"
{"cabinClass":"business","passenger":{"firstName":"Smoke","lastName":"Tester","passport":"SMOKE01"}}
"@) 200

Section "List bookings by passport"
Show-Result (Invoke-Api Get "$BaseUrl/api/v1/bookings?passport=SMOKE01") 200

Section "GET missing booking -> 404 BOOKING_NOT_FOUND"
Show-Result (Invoke-Api Get "$BaseUrl/api/v1/bookings/ZZZZZZ") 404 "BOOKING_NOT_FOUND"

# ---------------------------------------------------------------------------
Section "Check-in happy path (GZT001, departure tomorrow)"
Show-Result (Invoke-Api Post "$BaseUrl/api/v1/bookings/GZT001/check-in") 200

Section "Double check-in -> 409 ALREADY_CHECKED_IN"
Show-Result (Invoke-Api Post "$BaseUrl/api/v1/bookings/GZT001/check-in") 409 "ALREADY_CHECKED_IN"

Section "Baggage drop without check-in (LMN789 never checked in) -> 409 CHECKIN_REQUIRED"
Show-Result (Invoke-Api Post "$BaseUrl/api/v1/bookings/LMN789/baggage" '{"type":"checked","weightKg":15,"color":"blue"}') 409 "CHECKIN_REQUIRED"

Section "Check-in inside cutoff (LMN789, ~29 min before departure) -> 409 CHECKIN_CLOSED"
Show-Result (Invoke-Api Post "$BaseUrl/api/v1/bookings/LMN789/check-in") 409 "CHECKIN_CLOSED"

Section "Boundary: exactly at allowance is accepted (economy carry-on 8 kg)"
Show-Result (Invoke-Api Post "$BaseUrl/api/v1/bookings/GZT001/baggage" '{"type":"carryOn","weightKg":8,"color":"grey","hasLaptop":false,"fitsUnderSeat":true}') 201

Section "Overweight economy checked bag 24.9 kg (limit 23) -> 201 + translated warning"
$overweight = Invoke-Api Post "$BaseUrl/api/v1/bookings/GZT001/baggage" '{"type":"checked","weightKg":24.9,"color":"red","lengthCm":75,"widthCm":48,"heightCm":28,"fragile":false}'
Show-Result $overweight 201
$warnings = ($overweight.Content | ConvertFrom-Json).warnings
if ($warnings.Count -gt 0 -and $warnings[0].code -eq "CHECKED_BAGGAGE_OVERWEIGHT" -and $warnings[0].legacyMessage) {
    $script:pass++
    Write-Host "PASS warning translation: '$($warnings[0].legacyMessage)' -> code=$($warnings[0].code) actualKg=$($warnings[0].actualKg) limitKg=$($warnings[0].limitKg)" -ForegroundColor Green
}
else {
    $script:fail++
    Write-Host "FAIL warning was not translated correctly" -ForegroundColor Red
}

Section "Invalid baggage weight (-5 kg) -> backend INVALID_REQUEST translated to 400"
Show-Result (Invoke-Api Post "$BaseUrl/api/v1/bookings/GZT001/baggage" '{"type":"checked","weightKg":-5}') 400 "INVALID_REQUEST"

Section "Polymorphic body without discriminator -> framework-level 400"
Show-Result (Invoke-Api Post "$BaseUrl/api/v1/bookings/GZT001/baggage" '{"weightKg":10}') 400

Section "Flight designator, terminal and gate visibility (nested data + enums + nullable)"
$flightScheduled = $allFlights | Where-Object { $_.status -eq 'scheduled' -and $_.gate -eq $null } | Select-Object -First 1
$flightBoarding = $allFlights | Where-Object { $_.status -eq 'checkInOpen' -or $_.status -eq 'boarding' } | Select-Object -First 1
if ($flightScheduled -and $flightScheduled.designator.carrier -eq 'hb' -and $flightScheduled.departureTerminal -and $flightScheduled.from.terminal) {
    $script:pass++; Write-Host "PASS scheduled flight has designator enum, terminal nested, gate null ($($flightScheduled.flightNumber) gate=$($flightScheduled.gate))" -ForegroundColor Green
} else { $script:fail++; Write-Host "FAIL gate/terminal/designator visibility for scheduled" -ForegroundColor Red; Write-Host ($flightScheduled | ConvertTo-Json -Compress) -ForegroundColor DarkGray }
if ($flightBoarding -and $flightBoarding.gate) {
    $script:pass++; Write-Host "PASS boarding flight has gate populated ($($flightBoarding.flightNumber) gate=$($flightBoarding.gate) terminal=$($flightBoarding.departureTerminal))" -ForegroundColor Green
} else { $script:fail++; Write-Host "FAIL boarding gate should be non-null" -ForegroundColor Red }

Section "One-shot check-in with polymorphic bag array (checked xsi:type + carryOn) - REST"
$secondFlight = ($allFlights | Where-Object { $_.status -eq 'scheduled' -and $_.flightNumber -ne $flightNumber } | Select-Object -First 1).flightNumber
$sc = $secondFlight.Substring(0,2).ToLower(); $sn = [int]$secondFlight.Substring(2)
$polyBagBookingCreate = Invoke-Api Post "$BaseUrl/api/v1/bookings" @"
{"flight":{"carrier":"$sc","number":$sn},"cabinClass":"business","passenger":{"firstName":"Poly","lastName":"Array","passport":"POLY01"}}
"@
$polyRef = ($polyBagBookingCreate.Content | ConvertFrom-Json).bookingRef
"One-shot booking $polyRef on $secondFlight"
$oneShot = Invoke-Api Post "$BaseUrl/api/v1/bookings/$polyRef/check-in" '{"bags":[{"type":"checked","weightKg":22,"color":"blue","lengthCm":70,"widthCm":45,"heightCm":28,"fragile":false},{"type":"carryOn","weightKg":7,"color":"grey","hasLaptop":true,"fitsUnderSeat":false}]}'
Show-Result $oneShot 200
$oneShotBody = $oneShot.Content | ConvertFrom-Json
if ($oneShotBody.baggage.Count -eq 2 -and ($oneShotBody.baggage | Where-Object type -eq "checked").lengthCm -eq 70 -and ($oneShotBody.baggage | Where-Object type -eq "carryOn").hasLaptop -eq $true) {
    $script:pass++; Write-Host "PASS polymorphic array accepted with type-specific fields" -ForegroundColor Green
} else { $script:fail++; Write-Host "FAIL polymorphic array fields missing" -ForegroundColor Red; Write-Host $oneShot.Content -ForegroundColor DarkGray }

Section "One-shot duplicate type in array -> 409 BAGGAGE_TYPE_LIMIT"
$dupFlight = ($allFlights | Where-Object { $_.status -eq 'scheduled' -and $_.flightNumber -notin @($flightNumber,$secondFlight) } | Select-Object -First 1).flightNumber
$dc = $dupFlight.Substring(0,2).ToLower(); $dn = [int]$dupFlight.Substring(2)
$dupCreate = Invoke-Api Post "$BaseUrl/api/v1/bookings" @"
{"flight":{"carrier":"$dc","number":$dn},"cabinClass":"economy","passenger":{"firstName":"Dup","lastName":"Array","passport":"DUP01"}}
"@
$dupRef = ($dupCreate.Content | ConvertFrom-Json).bookingRef
Show-Result (Invoke-Api Post "$BaseUrl/api/v1/bookings/$dupRef/check-in" '{"bags":[{"type":"checked","weightKg":10},{"type":"checked","weightKg":12}]}') 409 "BAGGAGE_TYPE_LIMIT"

Section "Single-bag duplicate after check-in -> 409 BAGGAGE_TYPE_LIMIT"
Show-Result (Invoke-Api Post "$BaseUrl/api/v1/bookings/$polyRef/baggage" '{"type":"checked","weightKg":15}') 409 "BAGGAGE_TYPE_LIMIT"

# ---------------------------------------------------------------------------
Section "Create booking on cancelled flight -> 409 FLIGHT_CANCELLED"
$cancelledFlight = ($allFlights | Where-Object { $_.isCancelled } | Select-Object -First 1)
if (-not $cancelledFlight) {
    Write-Host "FAIL no cancelled flight in schedule" -ForegroundColor Red
    exit 1
}
"Using cancelled flight $($cancelledFlight.flightNumber)"
$ccCarrier = $cancelledFlight.flightNumber.Substring(0,2).ToLower()
$ccNum = [int]$cancelledFlight.flightNumber.Substring(2)
Show-Result (Invoke-Api Post "$BaseUrl/api/v1/bookings" @"
{"flight":{"carrier":"$ccCarrier","number":$ccNum},"cabinClass":"economy","passenger":{"firstName":"Smoke","lastName":"Tester","passport":"SMOKE01"}}
"@) 409 "FLIGHT_CANCELLED"

Section "Create booking on unknown flight -> 404 FLIGHT_NOT_FOUND"
Show-Result (Invoke-Api Post "$BaseUrl/api/v1/bookings" '{"flight":{"carrier":"hb","number":99999},"cabinClass":"economy","passenger":{"firstName":"Smoke","lastName":"Tester","passport":"SMOKE01"}}') 404 "FLIGHT_NOT_FOUND"

Section "Missing passport query -> framework-level 400"
Show-Result (Invoke-Api Get "$BaseUrl/api/v1/bookings") 400

# ---------------------------------------------------------------------------
Section "Chaos injection via REST header relay: 'unavailable' -> backend crash -> 502 LEGACY_UNAVAILABLE"
Show-Result (Invoke-Api Get "$BaseUrl/api/v1/flights/HB903" -Headers @{ "X-HB-Simulate" = "unavailable" }) 502 "LEGACY_UNAVAILABLE"

Section "Chaos injection: 'fault' -> typed INTERNAL_ERROR fault -> 502"
Show-Result (Invoke-Api Get "$BaseUrl/api/v1/flights/HB903" -Headers @{ "X-HB-Simulate" = "fault" }) 502 "INTERNAL_ERROR"

Section "Chaos injection: 'timeout=12' exceeds 10 s client timeout -> 504 LEGACY_TIMEOUT"
Show-Result (Invoke-Api Get "$BaseUrl/api/v1/flights/HB903" -Headers @{ "X-HB-Simulate" = "timeout=12" }) 504 "LEGACY_TIMEOUT"

# ---------------------------------------------------------------------------
Section "Raw SOAP 1.1 call straight into the legacy endpoint (bypassing middleware)"
$envelope = @"
<?xml version="1.0" encoding="utf-8"?>
<soapenv:Envelope xmlns:soapenv="http://schemas.xmlsoap.org/soap/envelope/" xmlns:lug="http://hummingbird.airlines/luggage">
  <soapenv:Header/>
  <soapenv:Body>
    <lug:CheckIn>
      <lug:bookingRef>QWX452</lug:bookingRef>
      <lug:bags/>
    </lug:CheckIn>
  </soapenv:Body>
</soapenv:Envelope>
"@
try {
    $soapResponse = Invoke-WebRequest -Uri "$BaseUrl/soap/luggage" -Method Post `
        -ContentType "text/xml; charset=utf-8" `
        -Headers @{ "SOAPAction" = '"http://hummingbird.airlines/luggage/CheckIn"' } `
        -Body $envelope -TimeoutSec 30 -SkipHttpErrorCheck
    if ($soapResponse.StatusCode -eq 200 -and $soapResponse.Content -match "CheckInResponse" -and $soapResponse.Content -match "QWX452") {
        $script:pass++
        Write-Host "PASS raw SOAP CheckIn returned boarding pass" -ForegroundColor Green
        $preview = $soapResponse.Content
        if ($preview.Length -gt 700) { $preview = $preview.Substring(0, 700) + " ..." }
        Write-Host $preview -ForegroundColor DarkGray
    }
    else {
        $script:fail++
        Write-Host "FAIL raw SOAP call: $($soapResponse.StatusCode)" -ForegroundColor Red
        Write-Host $soapResponse.Content -ForegroundColor DarkGray
    }
}
catch {
    $script:fail++
    Write-Host "FAIL raw SOAP call: $($_.Exception.Message)" -ForegroundColor Red
}

Section "Direct SOAP late check-in (LMN789) returns a real SOAP fault, not HTTP status semantics"
$faultEnvelope = $envelope -replace "QWX452", "LMN789"
$soapFault = Invoke-WebRequest -Uri "$BaseUrl/soap/luggage" -Method Post `
    -ContentType "text/xml; charset=utf-8" `
    -Headers @{ "SOAPAction" = '"http://hummingbird.airlines/luggage/CheckIn"' } `
    -Body $faultEnvelope -TimeoutSec 30 -SkipHttpErrorCheck
if ($soapFault.Content -match "CHECKIN_CLOSED" -and $soapFault.Content -match "faultcode") {
    $script:pass++
    Write-Host "PASS backend returned soap fault containing CHECKIN_CLOSED (HTTP $($soapFault.StatusCode))" -ForegroundColor Green
}
else {
    $script:fail++
    Write-Host "FAIL expected CHECKIN_CLOSED soap fault" -ForegroundColor Red
    Write-Host $soapFault.Content -ForegroundColor DarkGray
}

Section "Raw SOAP polymorphic array via xsi:type (checked + carryOn in one CheckIn)"
# create a booking for SOAP polymorphic test
$soapPolyFlight = ($allFlights | Where-Object { $_.status -eq 'scheduled' -and $_.flightNumber -notin @($flightNumber,$secondFlight,$dupFlight) } | Select-Object -First 1).flightNumber
$soapCarrier = $soapPolyFlight.Substring(0,2).ToLower(); $soapNum = [int]$soapPolyFlight.Substring(2)
$soapPolyCreate = Invoke-Api Post "$BaseUrl/api/v1/bookings" @"
{"flight":{"carrier":"$soapCarrier","number":$soapNum},"cabinClass":"economy","passenger":{"firstName":"Soap","lastName":"Poly","passport":"SOAP01"}}
"@
$soapPolyRef = ($soapPolyCreate.Content | ConvertFrom-Json).bookingRef
"SOAP poly booking $soapPolyRef on $soapPolyFlight"
$soapPolyEnvelope = @"
<?xml version="1.0" encoding="utf-8"?>
<soapenv:Envelope xmlns:soapenv="http://schemas.xmlsoap.org/soap/envelope/" xmlns:lug="http://hummingbird.airlines/luggage">
  <soapenv:Header/>
  <soapenv:Body>
    <lug:CheckIn>
      <lug:bookingRef>$soapPolyRef</lug:bookingRef>
      <lug:bags xmlns:b="http://schemas.datacontract.org/2004/07/Hummingbird.Airlines.Backend.Domain" xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
        <b:Baggage i:type="b:CheckedBaggage"><b:WeightKg>22</b:WeightKg><b:Color>navy</b:Color><b:TagId></b:TagId><b:LengthCm>70</b:LengthCm><b:WidthCm>45</b:WidthCm><b:HeightCm>28</b:HeightCm><b:Fragile>false</b:Fragile></b:Baggage>
        <b:Baggage i:type="b:CarryOnBaggage"><b:WeightKg>7</b:WeightKg><b:Color>grey</b:Color><b:TagId></b:TagId><b:HasLaptop>true</b:HasLaptop><b:FitsUnderSeat>false</b:FitsUnderSeat></b:Baggage>
      </lug:bags>
    </lug:CheckIn>
  </soapenv:Body>
</soapenv:Envelope>
"@
try {
    $soapPolyResp = Invoke-WebRequest -Uri "$BaseUrl/soap/luggage" -Method Post `
        -ContentType "text/xml; charset=utf-8" `
        -Headers @{ "SOAPAction" = '"http://hummingbird.airlines/luggage/CheckIn"' } `
        -Body $soapPolyEnvelope -TimeoutSec 30 -SkipHttpErrorCheck
    if ($soapPolyResp.StatusCode -eq 200 -and $soapPolyResp.Content -match "CheckedBaggage" -and $soapPolyResp.Content -match "CarryOnBaggage") {
        $script:pass++
        Write-Host "PASS SOAP polymorphic array with xsi:type returned both bag types" -ForegroundColor Green
    } else {
        $script:fail++
        Write-Host "FAIL SOAP polymorphic array: $($soapPolyResp.StatusCode)" -ForegroundColor Red
        Write-Host $soapPolyResp.Content -ForegroundColor DarkGray
    }
} catch {
    $script:fail++
    Write-Host "FAIL SOAP polymorphic array: $($_.Exception.Message)" -ForegroundColor Red
}

Section "WSDL is published for spec importers"
foreach ($endpoint in @("booking", "flights", "luggage")) {
    try {
        $wsdl = Invoke-WebRequest -Uri "$BaseUrl/soap/$endpoint`?wsdl" -TimeoutSec 30
        if ($wsdl.StatusCode -eq 200 -and $wsdl.Content -match "wsdl:definitions") {
            $script:pass++
            Write-Host "PASS /soap/$endpoint`?wsdl" -ForegroundColor Green
        }
        else {
            $script:fail++
            Write-Host "FAIL /soap/$endpoint`?wsdl (HTTP $($wsdl.StatusCode))" -ForegroundColor Red
        }
    }
    catch {
        $script:fail++
        Write-Host "FAIL /soap/$endpoint`?wsdl : $($_.Exception.Message)" -ForegroundColor Red
    }
}

Section "OpenAPI exposes the baggage polymorphic hierarchy (item + array)"
$swagger = Invoke-RestMethod "$BaseUrl/swagger/v1/swagger.json"
function Resolve-Schema {
    param($Schema, $Swagger)
    if ($Schema.'$ref') {
        $name = $Schema.'$ref' -replace '^#/components/schemas/', ''
        return $Swagger.components.schemas.$name
    }
    return $Schema
}
$baggageRequest = Resolve-Schema ($swagger.paths.'/api/v1/bookings/{reference}/baggage'.post.requestBody.content.'application/json'.schema) $swagger
$checkInSchema = Resolve-Schema ($swagger.paths.'/api/v1/bookings/{reference}/check-in'.post.requestBody.content.'application/json'.schema) $swagger
$checkInItems = Resolve-Schema $checkInSchema.properties.bags.items $swagger
if ($baggageRequest.oneOf) {
    $script:pass++
    $refs = $baggageRequest.oneOf.'$ref' -join ", "
    Write-Host "PASS baggage single-item schema is polymorphic oneOf ($refs)" -ForegroundColor Green
} else { $script:fail++; Write-Host "FAIL baggage single-item schema not polymorphic" -ForegroundColor Red }
if ($checkInItems.oneOf) {
    $script:pass++
    $refs2 = $checkInItems.oneOf.'$ref' -join ", "
    Write-Host "PASS check-in bag-array items are polymorphic oneOf ($refs2)" -ForegroundColor Green
} else { $script:fail++; Write-Host "FAIL check-in polymorphic array not exposed" -ForegroundColor Red }
if ($baggageRequest.discriminator -and $baggageRequest.discriminator.propertyName -eq 'type') {
    $script:pass++
    Write-Host "PASS baggage discriminator declares propertyName 'type'" -ForegroundColor Green
} else { $script:fail++; Write-Host "FAIL baggage discriminator missing" -ForegroundColor Red }
if ($baggageRequest.discriminator.mapping -and $baggageRequest.discriminator.mapping.checked -and $baggageRequest.discriminator.mapping.carryOn) {
    $script:pass++
    Write-Host "PASS baggage discriminator mapping checked/carryOn present" -ForegroundColor Green
} else { $script:fail++; Write-Host "FAIL baggage discriminator mapping missing" -ForegroundColor Red }
if ($swagger.components.schemas.CheckedBaggage.properties.type.enum[0] -eq 'checked' -and
    $swagger.components.schemas.CarryOnBaggage.properties.type.enum[0] -eq 'carryOn') {
    $script:pass++
    Write-Host "PASS derived schemas declare single-value type enum" -ForegroundColor Green
} else { $script:fail++; Write-Host "FAIL derived type enums missing" -ForegroundColor Red }

# ---------------------------------------------------------------------------
Section "Reset restores fresh state (POST /api/v1/admin/reset)"
$resetProbe = Invoke-Api Post "$BaseUrl/api/v1/bookings" @"
{"flight":{"carrier":"$carrier","number":$number},"cabinClass":"economy","passenger":{"firstName":"Reset","lastName":"Probe","passport":"RESET01"}}
"@
Show-Result $resetProbe 201
$resetProbeRef = ($resetProbe.Content | ConvertFrom-Json).bookingRef
$reset = Invoke-Api Post "$BaseUrl/api/v1/admin/reset"
Show-Result $reset 200
$resetBody = $reset.Content | ConvertFrom-Json
if ($resetBody.reset -eq $true -and $resetBody.demoBookings.Count -eq 5 -and $resetBody.demoBookings -contains "GZT001") {
    $script:pass++
    Write-Host "PASS reset returned 5 demo refs" -ForegroundColor Green
}
else {
    $script:fail++
    Write-Host "FAIL reset body unexpected" -ForegroundColor Red
    Write-Host $reset.Content -ForegroundColor DarkGray
}
Show-Result (Invoke-Api Get "$BaseUrl/api/v1/bookings/$resetProbeRef") 404 "BOOKING_NOT_FOUND"
Show-Result (Invoke-Api Get "$BaseUrl/api/v1/bookings/GZT001") 200
$afterReset = Invoke-Api Post "$BaseUrl/api/v1/bookings" @"
{"flight":{"carrier":"$carrier","number":$number},"cabinClass":"economy","passenger":{"firstName":"Reset","lastName":"Probe2","passport":"RESET02"}}
"@
Show-Result $afterReset 201
if (($afterReset.Content | ConvertFrom-Json).bookingRef -eq "T00001") {
    $script:pass++
    Write-Host "PASS ref counter restarted at T00001" -ForegroundColor Green
}
else {
    $script:fail++
    Write-Host "FAIL ref counter did not restart" -ForegroundColor Red
    Write-Host $afterReset.Content -ForegroundColor DarkGray
}

# ---------------------------------------------------------------------------
if (-not $SkipEvictionTest) {
    Section "Capacity: push past 50 bookings, oldest record is evicted"
    $evCarrier = $flightNumber.Substring(0,2).ToLower()
    $evNumber = [int]$flightNumber.Substring(2)
    for ($i = 0; $i -lt 55; $i++) {
        $null = Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/v1/bookings" `
            -ContentType "application/json" `
            -Body (@{ flight = @{ carrier = $evCarrier; number = $evNumber }; cabinClass = "economy";
                      passenger = @{ firstName = "Flood$i"; lastName = "Evict"; passport = "FLOOD$i" } } | ConvertTo-Json -Depth 6)
    }
    Show-Result (Invoke-Api Get "$BaseUrl/api/v1/bookings/GZT001") 404 "BOOKING_NOT_FOUND"
}

# ---------------------------------------------------------------------------
Section "Abuse protection: external-looking burst exceeds per-IP quota -> 429"
$limited = 0
for ($i = 0; $i -lt 130; $i++) {
    $burst = Invoke-WebRequest -Uri "$BaseUrl/api/v1/flights/HB900" `
        -Headers @{ "X-Forwarded-For" = "203.0.113.77" } `
        -TimeoutSec 30 -SkipHttpErrorCheck
    if ($burst.StatusCode -eq 429) { $limited++ }
}
if ($limited -gt 0) {
    $script:pass++
    Write-Host "PASS burst throttled ($limited of 130 requests rejected with 429)" -ForegroundColor Green
}
else {
    $script:fail++
    Write-Host "FAIL no request was rate limited" -ForegroundColor Red
}

Section "Abuse protection: internal loopback traffic stays exempt"
Show-Result (Invoke-Api Get "$BaseUrl/api/v1/flights/HB900") 200

Section "Default document lists the four services"
$page = Invoke-WebRequest -Uri "$BaseUrl/" -TimeoutSec 30
if ($page.StatusCode -eq 200 -and $page.Content -match "Service Directory" -and $page.Content -match "/soap/booking\?wsdl") {
    $script:pass++
    Write-Host "PASS landing page served with service directory and WSDL links" -ForegroundColor Green
}
else {
    $script:fail++
    Write-Host "FAIL landing page missing or incomplete" -ForegroundColor Red
}

""
"==============================="
"PASSED: $script:pass   FAILED: $script:fail"
"==============================="
exit ($script:fail -eq 0 ? 0 : 1)

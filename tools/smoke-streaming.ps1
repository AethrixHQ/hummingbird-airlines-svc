# Hummingbird Airlines streaming protocol smoke test (additive to smoke.ps1):
#   * REST v2 baggage claim board (snapshot + lookup + 404)
#   * HTTP/SSE live stream with keepalives and Last-Event-ID resumption
#   * gRPC unary + server streaming on the dedicated HTTP/2 port (via tools/GrpcProbe)
#   * gRPC bidirectional bag scanner (disposition matrix + half-close summary)
#   * REST gate turnaround snapshot + interactive WebSocket protocol
#   * GraphQL query + error code + mutations + graphql-transport-ws subscription + the
#     v2 sibling schema with oneOf bag variants (/graphql/v2)
#   * spec endpoints for importers (/specs/asyncapi.yaml, /specs/*.proto, /graphql/schema.graphql)
# Usage: ./tools/smoke-streaming.ps1 [-BaseUrl http://localhost:5000] [-GrpcPort 5007] [-GrpcProbeUrl http://localhost:5007]
# The board is clock-derived, so no reset is required; runs are independent.
# NOTE: requires PowerShell 7 (pwsh) - JSON ternaries and ClientWebSocket scripting.
# Set $env:ASPNETCORE_HTTP_PORTS=5000 before launching the service under test.

param(
    [string]$BaseUrl = "http://localhost:5000",
    [int]$GrpcPort = 5007,
    [string]$GrpcProbeUrl = ""
)

if (-not $GrpcProbeUrl) { $GrpcProbeUrl = "http://127.0.0.1:$GrpcPort" }

$script:pass = 0
$script:fail = 0

function Section([string]$title) {
    ""
    "=== $title ==="
}

function Pass([string]$label) {
    $script:pass++
    Write-Host "PASS $label" -ForegroundColor Green
}

function Fail([string]$label, $detail = "") {
    $script:fail++
    Write-Host "FAIL $label" -ForegroundColor Red
    if ($detail) { Write-Host "     $detail" -ForegroundColor DarkGray }
}

# --- WebSocket helpers (frames are tiny; a per-read wall-clock cap via WaitAny works
# --- around urocancellable quirks while abandoning incomplete receives harmlessly).
function Send-WsText([System.Net.WebSockets.ClientWebSocket]$Ws, [string]$Text) {
    $bytes = [Text.Encoding]::UTF8.GetBytes($Text)
    $Ws.SendAsync([ArraySegment[byte]]$bytes, [System.Net.WebSockets.WebSocketMessageType]::Text, $true, [Threading.CancellationToken]::None).Wait()
}

function Read-WsText([System.Net.WebSockets.ClientWebSocket]$Ws, [int]$TimeoutSeconds) {
    [byte[]]$buffer = [byte[]]::new(8192)
    $segment = [ArraySegment[byte]]$buffer
    $task = $Ws.ReceiveAsync($segment, [Threading.CancellationToken]::None)
    if ([Threading.Tasks.Task]::WaitAny([Threading.Tasks.Task[]]@([Threading.Tasks.Task]$task), [TimeSpan]::FromSeconds($TimeoutSeconds)) -ne 0) {
        return $null
    }
    $result = $task.Result
    if ($result.MessageType -eq [System.Net.WebSockets.WebSocketMessageType]::Close) { return $null }
    if ($result.EndOfMessage) { $text = [Text.Encoding]::UTF8.GetString($buffer, 0, $result.Count) } else {
        $stream = [IO.MemoryStream]::new()
        $stream.Write($buffer, 0, $result.Count)
        while ($true) {
            $task = $Ws.ReceiveAsync($segment, [Threading.CancellationToken]::None)
            if ([Threading.Tasks.Task]::WaitAny([Threading.Tasks.Task[]]@([Threading.Tasks.Task]$task), [TimeSpan]::FromSeconds($TimeoutSeconds)) -ne 0) { return $null }
            $result = $task.Result
            if ($result.MessageType -eq [System.Net.WebSockets.WebSocketMessageType]::Close) { return $null }
            $stream.Write($buffer, 0, $result.Count)
            if ($result.EndOfMessage) { $text = [Text.Encoding]::UTF8.GetString($stream.ToArray()); break }
        }
    }
    return $text
}

# ---------------------------------------------------------------------------
Section "Board snapshot via REST (GET /api/v2/baggage-claim?speed=60)"
try {
    $board = (Invoke-RestMethod "$BaseUrl/api/v2/baggage-claim?speed=60" -TimeoutSec 15)
    if ($board.boardId -eq 'CDG-T2E' -and $board.airport.code -eq 'CDG' -and $board.belts.Count -eq 5 -and $board.arrivals.Count -ge 1) {
        Pass "snapshot board=$($board.boardId) arrivals=$($board.arrivals.Count) speed=$($board.speed)"
    }
    else { Fail "snapshot shape unexpected" ($board | ConvertTo-Json -Depth 3) }
}
catch { Fail "snapshot request threw" $_.Exception.Message }

# ---------------------------------------------------------------------------
Section "Arrival lookup (GET /api/v2/baggage-claim/{flightNumber})"
$firstArrival = ($board.arrivals | Where-Object { $_.belt -gt 0 } | Select-Object -First 1)
if (-not $firstArrival) { $firstArrival = $board.arrivals | Select-Object -First 1 }
try {
    $arrival = Invoke-RestMethod "$BaseUrl/api/v2/baggage-claim/$($firstArrival.flightNumber)?speed=60" -TimeoutSec 10
    if ($arrival.flightNumber -ieq $firstArrival.flightNumber) {
        Pass "found $($arrival.flightNumber) airline=$($arrival.airline) state=$($arrival.state) belt=$($arrival.belt)"
    }
    else { Fail "lookup returned another flight" }
}
catch { Fail "lookup threw" $_.Exception.Message }

try {
    Invoke-RestMethod "$BaseUrl/api/v2/baggage-claim/ZZ0000?speed=60" -TimeoutSec 10 | Out-Null
    Fail "unknown flight returned a 2xx"
}
catch {
    $response = $_.Exception.Response
    $status = [int]$response.StatusCode
    if ($status -eq 404) { $body = $_.ErrorDetails.Message; if ($body -match 'ARRIVAL_NOT_FOUND') { Pass "unknown flight -> 404 ARRIVAL_NOT_FOUND" } else { Pass "unknown flight -> 404" } }
    else { Fail "unknown flight -> $status expected 404" }
}

# ---------------------------------------------------------------------------
Section "SSE live stream (GET /api/v2/baggage-claim/stream?speed=60, 15 s sample)"
$null = Get-Command curl.exe -ErrorAction SilentlyContinue
if ($?) {
    $sse = & curl.exe -s -N --max-time 15 "$BaseUrl/api/v2/baggage-claim/stream?speed=60" 2>&1
    $text = $sse -join "`n"
    $hasRetry = $text -match 'retry: 3000'
    $hasSnapshot = $text -match 'event: snapshot'
    $hasEvent = $text -match 'event: (flightInbound|flightArrived|unloadingStarted|lastBag|beltReleased|flightOffBoard)'
    $hasKeepalive = $text -match ': keepalive'
    if ($hasRetry -and $hasSnapshot) { Pass "SSE framing (retry + snapshot event)" }
    else { Fail "SSE framing incomplete" "retry=$hasRetry snapshot=$hasSnapshot" }
    if ($hasEvent) { Pass "SSE change events started flowing (speed=60 cadence ~6 s)" }
    else { Fail "no change event within the sample window" }
    if ($hasKeepalive) { Pass "SSE keepalive comment observed" }
    else { Write-Host "note: no keepalive inside the short sample (expected every 15 s)" -ForegroundColor DarkYellow }
}
else { Fail "curl.exe unavailable for SSE sampling" }

# ---------------------------------------------------------------------------
Section "SSE resumption (Last-Event-ID: 1 replays recent board history)"
try {
    $sseReplay = & curl.exe -s -N --max-time 6 -H "Last-Event-ID: 1" "$BaseUrl/api/v2/baggage-claim/stream?speed=60" 2>&1
    $replayText = $sseReplay -join "`n"
    $snapshotIdx = $replayText.IndexOf('event: snapshot', [System.StringComparison]::Ordinal)
    $later = $replayText.Substring([Math]::Max($snapshotIdx, 0))
    $replayed = $later -match 'event: '
    if ($snapshotIdx -ge 0 -and $replayed) { Pass "resume replayed at least one missed event after the snapshot" }
    else { Fail "resumption replay produced nothing" "" }
}
catch { Fail "SSE resumption threw" $_.Exception.Message }

# ---------------------------------------------------------------------------
Section "gRPC unary: BaggageClaim/GetBoard (HTTP/2 prior knowledge)"
$repoRoot = Split-Path -Parent $PSScriptRoot
if (Get-Command dotnet -ErrorAction SilentlyContinue) {
    Push-Location $repoRoot
    try {
        $grcpTable = & dotnet run --project tools\GrpcProbe -- $GrpcProbeUrl table 60 2>&1
        if ($LASTEXITCODE -eq 0 -and ($grcpTable -join "`n") -match 'belts\s+:') {
            Pass "GetBoard unary over gRPC port $GrpcPort"
        }
        else { Fail "gRPC table probe failed (exit $LASTEXITCODE)" ($grcpTable -join "`n") }
    }
    finally { Pop-Location }

    Section "gRPC server streaming: BaggageClaim/WatchBoard (10 s sample)"
    Push-Location $repoRoot
    try {
        $grpcWatch = & dotnet run --project tools\GrpcProbe -- $GrpcProbeUrl watch 10 60 2>&1
        $watchText = $grpcWatch -join "`n"
        $varianceOk = $watchText -match 'received \d+ update\(s\)'
        if ($LASTEXITCODE -eq 0 -and $varianceOk -and ($watchText -match 'snapshot CDG-T2E')) {
            Pass "WatchBoard stream opened + updates received"
        }
        else { Fail "gRPC watch probe failed (exit $LASTEXITCODE)" $watchText }
    }
    finally { Pop-Location }
}
else { Fail "dotnet CLI not found; gRPC probes skipped" }

# ---------------------------------------------------------------------------
Section "gRPC bidi scanner: BaggageScanner/ScanBags (probe demo session)"
if (Get-Command dotnet -ErrorAction SilentlyContinue) {
    Push-Location $repoRoot
    try {
        $scan = & dotnet run --project tools\GrpcProbe -- $GrpcProbeUrl scan 2>&1
        $scanText = $scan -join "`n"
        $summaryOk = $LASTEXITCODE -eq 0 -and ($scanText -match 'scanner-session-') -and ($scanText -match 'total=14\s+accepted=8\s+rejected=6')
        $kindsOk = ($scanText -match 'Duplicate') -and ($scanText -match 'Malformed') -and ($scanText -match 'UnknownZone') -and ($scanText -match 'LimitExceeded')
        if ($summaryOk -and $kindsOk) { Pass "ScanBags bidi session: 14 scans, every disposition exercised, summary emitted" }
        else { Fail "scanner probe unexpected" $scanText }
    }
    finally { Pop-Location }
}

# ---------------------------------------------------------------------------
Section "REST: gate turnaround snapshot (GET /api/v2/gates?speed=60)"
try {
    $gates = Invoke-RestMethod "$BaseUrl/api/v2/gates?speed=60" -TimeoutSec 15
    if ($gates.feedId -eq 'CDG-T2E-GATES' -and $gates.gates.Count -eq 8) { Pass "gate snapshot feedId=$($gates.feedId) gates=$($gates.gates.Count) speed=$($gates.speed)" }
    else { Fail "gate snapshot shape unexpected" ($gates | ConvertTo-Json -Depth 3) }
}
catch { Fail "gate snapshot threw" $_.Exception.Message }

# ---------------------------------------------------------------------------
Section "Spec endpoints for importers (AsyncAPI / protos / GraphQL SDL)"
try {
    $asyncapi = Invoke-WebRequest "$BaseUrl/specs/asyncapi.yaml" -UseBasicParsing -TimeoutSec 15
    $specText = ($asyncapi.Content -is [byte[]]) ? [Text.Encoding]::UTF8.GetString($asyncapi.Content) : "$($asyncapi.Content)"
    $asyncOk = ($asyncapi.StatusCode -eq 200) -and ($specText -match '(?m)^asyncapi: 3\.0\.3') -and ($specText -match 'gateTurnaroundStream') -and ($specText -match 'baggageClaimStream')
    if ($asyncOk) { Pass "/specs/asyncapi.yaml served (3.0.3, both channels)" }
    else { Fail "asyncapi spec incomplete" ($specText.Substring(0, [Math]::Min(200, $specText.Length))) }

    $protoClaim = Invoke-WebRequest "$BaseUrl/specs/baggageclaim.proto" -UseBasicParsing -TimeoutSec 15
    $protoScan = Invoke-WebRequest "$BaseUrl/specs/baggagescanner.proto" -UseBasicParsing -TimeoutSec 15
    if (($protoClaim.StatusCode -eq 200) -and ($protoClaim.Content -match 'service BaggageClaim') -and ($protoScan.StatusCode -eq 200) -and ($protoScan.Content -match 'rpc ScanBags\s*\(\s*stream')) {
        Pass "both proto contracts served (BaggageClaim + BaggageScanner)"
    }
    else { Fail "proto spec content wrong" }

    $sdl = Invoke-WebRequest "$BaseUrl/graphql/schema.graphql" -UseBasicParsing -TimeoutSec 15
    $sdlText = ($sdl.Content -is [byte[]]) ? [Text.Encoding]::UTF8.GetString($sdl.Content) : "$($sdl.Content)"
    if (($sdl.StatusCode -eq 200) -and ($sdlText -match 'subscription:\s*Subscription') -and ($sdlText -match 'bagClaimEvents') -and ($sdlText -match '(?m)^type Mutation \{') -and ($sdlText -match '(?m)^input ScanBagInput')) {
        Pass "/graphql/schema.graphql SDL (Query/Mutation/Subscription roots)"
    }
    else { Fail "GraphQL SDL incomplete" }

    try { Invoke-WebRequest "$BaseUrl/specs/notarealspec.proto" -UseBasicParsing -TimeoutSec 15 | Out-Null; Fail "unknown spec returned 2xx" }
    catch { if ([int]$_.Exception.Response.StatusCode -eq 404) { Pass "unknown proto -> 404 problem" } else { Fail "unknown proto -> unexpected status" } }
}
catch { Fail "spec endpoints threw" $_.Exception.Message }

# ---------------------------------------------------------------------------
Section "GraphQL query + error code (POST /graphql)"
try {
    $payload = '{"query":"{ bagClaimBoard(speed: 60) { boardId speed belts { number idle } arrivals { flightNumber belt } } }"}'
    $gql = Invoke-RestMethod "$BaseUrl/graphql" -Method Post -Body $payload -ContentType 'application/json' -TimeoutSec 20
    if ($gql.data.bagClaimBoard.boardId -eq 'CDG-T2E' -and $gql.data.bagClaimBoard.belts.Count -eq 5) {
        Pass "piecewise board query bagClaimBoard (compressed clock render)"
    } else { Fail "GraphQL board query shape unexpected" ($gql | ConvertTo-Json -Depth 4) }

    $errorPayload = '{"query":"{ bagClaimArrival(flightNumber: \"ZZ0000\", speed: 60) { flightNumber } }"}'
    $gqlErr = Invoke-RestMethod "$BaseUrl/graphql" -Method Post -Body $errorPayload -ContentType 'application/json' -TimeoutSec 20
    if ($gqlErr.errors -and ($gqlErr.errors[0].extensions.code -eq 'ARRIVAL_NOT_FOUND')) { Pass "missing arrival -> GraphQL error code ARRIVAL_NOT_FOUND" }
    else { Fail "missing arrival error code missing" ($gqlErr | ConvertTo-Json -Depth 4) }
}
catch { Fail "GraphQL POST threw" $_.Exception.Message }

# ---------------------------------------------------------------------------
Section "GraphQL mutations: same legacy paths + typed codes (checkIn / dropBag / scanBag)"
try {
    $null = Invoke-RestMethod "$BaseUrl/api/v1/admin/reset" -Method Post -TimeoutSec 15

    # (a) bag drop without prior check-in -> mapped legacy code
    $payload = '{"query":"mutation { dropBag(input: { bookingRef: \"LMN789\", bag: { kind: CHECKED, weightKg: 18 } }) { success } }"}'
    $gql = Invoke-RestMethod "$BaseUrl/graphql" -Method Post -Body $payload -ContentType 'application/json' -TimeoutSec 20
    if ($null -eq $gql.data.dropBag -and $gql.errors[0].extensions.code -eq 'CHECKIN_REQUIRED') {
        Pass "dropBag unchecked-in -> CHECKIN_REQUIRED (data null)"
    } else { Fail "dropBag CHECKIN_REQUIRED missing" ($gql | ConvertTo-Json -Depth 4) }

    # (b) fresh booking -> boarding pass with bags (checked + carry-on) via the same legacy path
    $flights = Invoke-RestMethod "$BaseUrl/api/v1/flights" -TimeoutSec 15
    $flight = ($flights | Where-Object { $_.status -eq 'scheduled' -and -not $_.isCancelled } |
        Sort-Object scheduledDepartureUtc | Select-Object -First 1)
    $nr = $flight.flightNumber
    $createBody = @{
        flight     = @{ carrier = $nr.Substring(0, 2).ToLower(); number = [int]$nr.Substring(2) }
        cabinClass = 'economy'
        passenger  = @{ firstName = 'Smoke'; lastName = 'Mutation'; passport = 'GQLSMOKE01' }
    } | ConvertTo-Json -Depth 5 -Compress
    $created = Invoke-RestMethod "$BaseUrl/api/v1/bookings" -Method Post -ContentType 'application/json' -Body $createBody -TimeoutSec 15
    $ref = $created.bookingRef

    $payload = '{"query":"mutation { checkIn(input: { bookingRef: \"' + $ref + '\", bags: [ { kind: CHECKED, weightKg: 28, color: \"teal\" }, { kind: CARRY_ON, weightKg: 6, hasLaptop: true } ] }) { bookingRef flightNumber seat gate boardingSequence bags { tagId kind weightKg hasLaptop } warnings { code } } }"}'
    $gql = Invoke-RestMethod "$BaseUrl/graphql" -Method Post -Body $payload -ContentType 'application/json' -TimeoutSec 30
    $ci = $gql.data.checkIn
    if ($null -ne $ci -and $ci.bookingRef -eq $ref -and $ci.seat -ne '' -and $ci.gate -ne '' `
        -and $ci.flightNumber -eq $nr -and $ci.boardingSequence -gt 0 `
        -and $ci.bags.Count -eq 2 -and $ci.bags[0].kind -eq 'CHECKED' -and $ci.bags[1].kind -eq 'CARRY_ON' `
        -and (($ci.bags | Where-Object { $_.tagId -like 'HB-*' }).Count -eq 2) `
        -and (($ci.warnings | Where-Object { $_.code -eq 'CHECKED_BAGGAGE_OVERWEIGHT' }).Count -eq 1)) {
        Pass "checkIn -> boarding pass $ref (atomically registered CHECKED + CARRY_ON, overweight warning)"
    }
    else { Fail "checkIn payload wrong" ($gql | ConvertTo-Json -Depth 4) }

    # (c) repeat check-in -> mapped legacy code
    $payload = '{"query":"mutation { checkIn(input: { bookingRef: \"' + $ref + '\" }) { bookingRef } }"}'
    $gql = Invoke-RestMethod "$BaseUrl/graphql" -Method Post -Body $payload -ContentType 'application/json' -TimeoutSec 20
    if ($gql.errors[0].extensions.code -eq 'ALREADY_CHECKED_IN') {
        Pass "repeat checkIn -> ALREADY_CHECKED_IN"
    }
    else { Fail "ALREADY_CHECKED_IN missing" ($gql | ConvertTo-Json -Depth 4) }

    # (d) second bag of an already-registered kind -> BAGGAGE_TYPE_LIMIT
    $payload = '{"query":"mutation { dropBag(input: { bookingRef: \"' + $ref + '\", bag: { kind: CHECKED, weightKg: 12 } }) { success } }"}'
    $gql = Invoke-RestMethod "$BaseUrl/graphql" -Method Post -Body $payload -ContentType 'application/json' -TimeoutSec 20
    if ($gql.errors[0].extensions.code -eq 'BAGGAGE_TYPE_LIMIT') {
        Pass "2nd checked bag -> BAGGAGE_TYPE_LIMIT"
    }
    else { Fail "BAGGAGE_TYPE_LIMIT missing" ($gql | ConvertTo-Json -Depth 4) }

    # (e) weight beyond the hard range -> INVALID_REQUEST
    $payload = '{"query":"mutation { dropBag(input: { bookingRef: \"' + $ref + '\", bag: { kind: CARRY_ON, weightKg: 150 } }) { success } }"}'
    $gql = Invoke-RestMethod "$BaseUrl/graphql" -Method Post -Body $payload -ContentType 'application/json' -TimeoutSec 20
    if ($gql.errors[0].extensions.code -eq 'INVALID_REQUEST') {
        Pass "150 kg bag -> INVALID_REQUEST"
    }
    else { Fail "INVALID_REQUEST missing" ($gql | ConvertTo-Json -Depth 4) }

    # (f) scanBag: session reset + dispositions (data, not errors) + deterministic FNV belt
    $null = Invoke-RestMethod "$BaseUrl/graphql" -Method Post -ContentType 'application/json' -TimeoutSec 20 `
        -Body '{"query":"mutation { resetScanSession(sessionId: \"smoke-gql-scan\") { removed } }"}'
    $hash = [uint32]2166136261
    foreach ($b in [Text.Encoding]::UTF8.GetBytes('AB123456')) {
        $hash = [uint64]$hash -bxor [uint64]$b
        $hash = ([uint64]$hash * [uint64]16777619) -band [uint64]4294967295
        $hash = [uint32]$hash
    }
    $expectedBelt = ([uint32]($hash % 5)) + 1
    $payload = '{"query":"mutation { scanBag(input: { sessionId: \"smoke-gql-scan\", scanId: \"S1\", tagNumber: \"AB123456\" }) { scanId disposition belt } }"}'
    $gql = Invoke-RestMethod "$BaseUrl/graphql" -Method Post -Body $payload -ContentType 'application/json' -TimeoutSec 20
    if ($gql.data.scanBag.disposition -eq 'ACCEPTED' -and $gql.data.scanBag.scanId -eq 'S1' -and ($gql.data.scanBag.belt -in 1..5) -and ($gql.data.scanBag.belt -eq $expectedBelt)) {
        Pass "scanBag AB123456 -> ACCEPTED (belt $($gql.data.scanBag.belt) = FNV-1a expectation)"
    }
    else { Fail "scanBag ACCEPTED missing" ($gql | ConvertTo-Json -Depth 4) }

    $payload = '{"query":"mutation { scanBag(input: { sessionId: \"smoke-gql-scan\", scanId: \"S2\", tagNumber: \"AB123456\" }) { disposition } }"}'
    $gql = Invoke-RestMethod "$BaseUrl/graphql" -Method Post -Body $payload -ContentType 'application/json' -TimeoutSec 20
    if ($gql.data.scanBag.disposition -eq 'DUPLICATE') { Pass "scanBag same tag -> DUPLICATE" }
    else { Fail "DUPLICATE missing" ($gql | ConvertTo-Json -Depth 4) }

    $payload = '{"query":"mutation { scanBag(input: { sessionId: \"smoke-gql-scan\", scanId: \"S3\", tagNumber: \"Q1\" }) { disposition } }"}'
    $gql = Invoke-RestMethod "$BaseUrl/graphql" -Method Post -Body $payload -ContentType 'application/json' -TimeoutSec 20
    if ($gql.data.scanBag.disposition -eq 'MALFORMED') { Pass "scanBag Q1 -> MALFORMED" }
    else { Fail "MALFORMED missing" ($gql | ConvertTo-Json -Depth 4) }

    $payload = '{"query":"mutation { scanBag(input: { sessionId: \"smoke-gql-scan\", scanId: \"S4\", tagNumber: \"AB123457\", zone: \"ZZ9\" }) { disposition } }"}'
    $gql = Invoke-RestMethod "$BaseUrl/graphql" -Method Post -Body $payload -ContentType 'application/json' -TimeoutSec 20
    if ($gql.data.scanBag.disposition -eq 'UNKNOWN_ZONE') { Pass "scanBag ZZ9 -> UNKNOWN_ZONE" }
    else { Fail "UNKNOWN_ZONE missing" ($gql | ConvertTo-Json -Depth 4) }

    $payload = '{"query":"mutation { scanBag(input: { sessionId: \" \", scanId: \"S0\", tagNumber: \"AB123456\" }) { disposition } }"}'
    $gql = Invoke-RestMethod "$BaseUrl/graphql" -Method Post -Body $payload -ContentType 'application/json' -TimeoutSec 20
    if ($gql.errors[0].extensions.code -eq 'INVALID_REQUEST') { Pass "blank sessionId -> INVALID_REQUEST" }
    else { Fail "blank sessionId error missing" ($gql | ConvertTo-Json -Depth 4) }

    $payload = '{"query":"mutation { resetScanSession(sessionId: \"smoke-gql-scan\") { removed } }"}'
    $gql = Invoke-RestMethod "$BaseUrl/graphql" -Method Post -Body $payload -ContentType 'application/json' -TimeoutSec 20
    if ($gql.data.resetScanSession.removed -eq $true) { Pass "resetScanSession -> cleared" }
    else { Fail "session reset missing" ($gql | ConvertTo-Json -Depth 4) }
}
catch { Fail "GraphQL mutations threw" $_.Exception.Message }

# ---------------------------------------------------------------------------
Section "GraphQL v2 sibling schema (oneOf bag variants on /graphql/v2)"
try {
    $sdlV2 = Invoke-WebRequest "$BaseUrl/graphql/v2/schema.graphql" -TimeoutSec 20
    $v2Sdl = if ($sdlV2.Content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($sdlV2.Content) } else { [string]$sdlV2.Content }
    if (($sdlV2.StatusCode -eq 200) -and ($v2Sdl -match '(?m)input BagInput') -and ($v2Sdl -match '@oneOf') `
        -and ($v2Sdl -match '(?m)input CheckedBagInput') -and ($v2Sdl -match '(?m)input CarryOnBagInput')) {
        Pass "v2 SDL: input BagInput @oneOf + CheckedBagInput/CarryOnBagInput"
    }
    else { Fail "v2 SDL oneOf shape missing" ($v2Sdl -split "`n" | Select-Object -First 1) }

    $sdlV1 = Invoke-WebRequest "$BaseUrl/graphql/schema.graphql" -TimeoutSec 20
    $v1Sdl = if ($sdlV1.Content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($sdlV1.Content) } else { [string]$sdlV1.Content }
    if (($sdlV1.StatusCode -eq 200) -and ($v1Sdl -notmatch 'oneOf') -and ($v1Sdl -match '(?m)input BagInput')) {
        Pass "v1 SDL stays flat (no oneOf directive)"
    }
    else { Fail "v1 SDL changed unexpectedly" ($v1Sdl -split "`n" | Select-Object -First 1) }

    # v2 checkIn with exactly-one-of variants (fresh booking) -> same legacy verdict as v1
    $flights = Invoke-RestMethod "$BaseUrl/api/v1/flights" -TimeoutSec 15
    $flight = ($flights | Where-Object { $_.status -eq 'scheduled' -and -not $_.isCancelled } |
        Sort-Object scheduledDepartureUtc | Select-Object -First 1)
    $nr = $flight.flightNumber
    $createBody = @{
        flight     = @{ carrier = $nr.Substring(0, 2).ToLower(); number = [int]$nr.Substring(2) }
        cabinClass = 'economy'
        passenger  = @{ firstName = 'Smoke'; lastName = 'OneOf'; passport = 'GQLONEOF1' }
    } | ConvertTo-Json -Depth 5 -Compress
    $created = Invoke-RestMethod "$BaseUrl/api/v1/bookings" -Method Post -ContentType 'application/json' -Body $createBody -TimeoutSec 15
    $refV2 = $created.bookingRef

    $payload = '{"query":"mutation { checkIn(input: { bookingRef: \"' + $refV2 + '\", bags: [ { checked: { weightKg: 28, color: \"teal\" } }, { carryOn: { weightKg: 6, hasLaptop: true } } ] }) { bookingRef seat bags { kind weightKg hasLaptop } warnings { code } } }"}'
    $gql = Invoke-RestMethod "$BaseUrl/graphql/v2" -Method Post -Body $payload -ContentType 'application/json' -TimeoutSec 30
    $ci = $gql.data.checkIn
    if ($null -ne $ci -and $ci.bookingRef -eq $refV2 -and $ci.bags.Count -eq 2 `
        -and $ci.bags[0].kind -eq 'CHECKED' -and $ci.bags[1].kind -eq 'CARRY_ON' `
        -and (($ci.warnings | Where-Object { $_.code -eq 'CHECKED_BAGGAGE_OVERWEIGHT' }).Count -eq 1)) {
        Pass "v2 checkIn (checked + carryOn variants) -> same payload and warning"
    }
    else { Fail "v2 checkIn parity missing" ($gql | ConvertTo-Json -Depth 4) }

    # both variants on one bag -> GraphQL-level oneOf coercion error (no data)
    $payload = '{"query":"mutation { checkIn(input: { bookingRef: \"' + $refV2 + '\", bags: [ { checked: { weightKg: 10 }, carryOn: { weightKg: 5 } } ] }) { bookingRef } }"}'
    $both = $false
    try {
        $gql = Invoke-RestMethod "$BaseUrl/graphql/v2" -Method Post -Body $payload -ContentType 'application/json' -TimeoutSec 20
        $both = ($null -eq $gql.data.checkIn) -and ($gql.errors.Count -gt 0)
    }
    catch { $both = ($_.ErrorDetails.Message -match 'exactly one field must be supplied') }
    if ($both) { Pass "both variants -> oneOf coercion error" }
    else { Fail "oneOf coercion error missing" ($gql | ConvertTo-Json -Depth 4) }

    # non-polymorphic operations keep their flat input shape on v2 too
    $payload = '{"query":"mutation { scanBag(input: { sessionId: \"smoke-gql-v2\", scanId: \"S2V\", tagNumber: \"AB123456\", zone: \"MU2\" }) { disposition belt } }"}'
    $gql = Invoke-RestMethod "$BaseUrl/graphql/v2" -Method Post -Body $payload -ContentType 'application/json' -TimeoutSec 20
    if ($gql.data.scanBag.disposition -eq 'ACCEPTED' -and ($gql.data.scanBag.belt -in 1..5)) {
        Pass "v2 scanBag unchanged (flat input, ACCEPTED)"
    }
    else { Fail "v2 scanBag missing" ($gql | ConvertTo-Json -Depth 4) }
}
catch { Fail "GraphQL v2 threw" $_.Exception.Message }

# ---------------------------------------------------------------------------
Section "GraphQL subscription over WebSocket (graphql-transport-ws, speed 60)"
$wsOk = $false
$wsEventOk = $false
try {
    $gqlWs = [System.Net.WebSockets.ClientWebSocket]::new()
    $gqlWs.Options.AddSubProtocol('graphql-transport-ws')
    $gqlWs.ConnectAsync([Uri]("$BaseUrl".Replace('http://','ws://') + '/graphql'), [Threading.CancellationToken]::None).Wait(5000) | Out-Null

    Send-WsText $gqlWs '{"type":"connection_init","payload":{}}'
    $ack = Read-WsText $gqlWs 8
    $wsOk = $ack -and (($ack | ConvertFrom-Json).type -in @('connection_ack', 'ka'))
    if ($wsOk) {
        Pass "graphql-transport-ws connection_init acknowledged"
        Send-WsText $gqlWs '{"id":"1","type":"subscribe","payload":{"query":"subscription { bagClaimEvents(speed: 60) { sequence kind flightNumber belt state } }"}}'
        for ($i = 1; $i -le 6; $i++) {
            $message = Read-WsText $gqlWs 15
            if (-not $message) { break }
            $parsed = $message | ConvertFrom-Json
            if ($parsed.type -eq 'next' -and $parsed.payload.data.bagClaimEvents) { $wsEventOk = $true; break }
            if ($parsed.type -eq 'error') { break }
        }
        if ($wsEventOk) { Pass "subscription delivered bagClaimEvents over transport-ws" } else { Fail "no subscription data within sample" }
    } else { Fail "graphql ws init not acknowledged" ($ack ?? '<null>') }
    $gqlWs.Dispose()
}
catch {
    Fail "graphql websocket threw" ($_.Exception.InnerException ?? $_).Message
}

# ---------------------------------------------------------------------------
Section "Gate feed WebSocket (subscribe/snapshot/error protocol on /api/v2/gates/ws)"
$gateWsOk = $false
$gateEventCaught = $false
$gateErrorOk = $false
try {
    $gateWs = [System.Net.WebSockets.ClientWebSocket]::new()
    $gateWs.ConnectAsync([Uri]("$BaseUrl".Replace('http://','ws://') + '/api/v2/gates/ws'), [Threading.CancellationToken]::None).Wait(5000) | Out-Null

    Send-WsText $gateWs '{"type":"subscribe","speed":60}'
    $snapshotText = Read-WsText $gateWs 10
    $snapshot = if ($snapshotText) { $snapshotText | ConvertFrom-Json } else { $null }
    $gateWsOk = $snapshot -and $snapshot.type -eq 'snapshot' -and $snapshot.feedId -eq 'CDG-T2E-GATES' -and $snapshot.gates.Count -eq 8
    if ($gateWsOk) { Pass "subscribe -> snapshot (8 gates, speed wired through)" }

    # events flow every ~2 wall seconds at speed 60; drain up to 8 frames for one gateEvent
    for ($i = 1; $i -le 8; $i++) {
        $frame = Read-WsText $gateWs 12
        if (-not $frame) { break }
        $frameObj = $frame | ConvertFrom-Json
        if ($frameObj.type -eq 'gateEvent') { $gateEventCaught = $true; break }
    }
    if ($gateEventCaught) { Pass "gateEvent pushed (sequence = feed-time ms)" } else { Fail "no gateEvent within sample" }

    # unknown command -> error envelope, socket stays open
    for ($tries = 0; $tries -lt 4; $tries++) {
        Send-WsText $gateWs '{"type":"instant-death"}'
        for ($i = 0; $i -lt 4; $i++) {
            $errFrame = Read-WsText $gateWs 8
            if (-not $errFrame) { break }
            $errObj = $errFrame | ConvertFrom-Json
            if ($errObj.type -eq 'error' -and $errObj.code -ieq 'unrecognized_type') { $gateErrorOk = $true; break }
        }
        if ($gateErrorOk) { break }
    }
    if ($gateErrorOk) { Pass "unrecognized type -> error envelope (connection stays open)" } else { Fail "no UNRECOGNIZED_TYPE error" }

    Send-WsText $gateWs '{"type":"unsubscribe"}'
    $gateWs.Dispose()
}
catch {
    Fail "gate websocket threw" ($_.Exception.InnerException ?? $_).Message
}

""
"==============================="
"PASSED: $script:pass   FAILED: $script:fail"
"==============================="
exit ($script:fail -eq 0 ? 0 : 1)

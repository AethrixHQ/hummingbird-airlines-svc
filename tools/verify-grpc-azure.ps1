# Post-quota-reset verification of gRPC (and HTTP/1.1 vs HTTP/2) through the Azure front end.
#
# Run after the F1 free-tier quota reset (00:00 UTC) once hummingbird-airline is serving:
#   pwsh -NoProfile -File tools/verify-grpc-azure.ps1
#
# Azure-side configuration this script expects/exercises (request group: stellar-qa-eu, app: hummingbird-airline):
#   * App setting HTTP20_ONLY_PORT = 5007            (front end proxies gRPC to the app's clear-text h2 listener)
#   * siteConfig http20ProxyFlag = 1 ("On")          (official App-Services-Linux gRPC recipe; portal value 2 = "gRPC Only")
#   * HTTP version 2.0 / http20Enabled = true        (client-facing h2 via ALPN)
# Revert proxy mode if needed:  az webapp update -g stellar-qa-eu -n hummingbird-airline --set siteConfig.http20ProxyFlag=2
# NOTE on full "On" mode: WebSocket upgrades may not survive an end-to-end h2 hop; the smoke-streaming
# section below explicitly checks the gate WebSocket and a graphql-transport-ws subscription for that.
# NOTE: requires PowerShell 7 (pwsh). Publishing-profile credentials fetched by 'az' are used in memory
# only for the Kudu log fetch on failure - never printed.
# Set $env:ASPNETCORE_HTTP_PORTS=5000 before pointing this script at a local server instead.

param(
    [string]$BaseUrl = "https://hummingbird-airline.azurewebsites.net",
    [string]$ResourceGroup = "stellar-qa-eu",
    [string]$WebAppName = "hummingbird-airline",
    [int]$UpTimeoutMinutes = 15,
    [int]$MaxGrpcAttempts = 3
)

$ErrorActionPreference = "Continue"
$script:pass = 0
$script:fail = 0

function Result([string]$label, [bool]$ok, [string]$detail = "") {
    if ($ok) {
        $script:pass++
        Write-Host "PASS $label" -ForegroundColor Green
    }
    else {
        $script:fail++
        Write-Host "FAIL $label" -ForegroundColor Red
        if ($detail) { Write-Host "     $detail" -ForegroundColor DarkGray }
    }
}

"=== waiting for $($BaseUrl) to come back online (up to $UpTimeoutMinutes min) ==="
$deadline = (Get-Date).AddMinutes($UpTimeoutMinutes)
$up = $false
while ((Get-Date) -lt $deadline) {
    try {
        $probe = Invoke-WebRequest "$BaseUrl/api/v1/_protection" -UseBasicParsing -TimeoutSec 10
        if ($probe.StatusCode -eq 200) { $up = $true; break }
    }
    catch { Start-Sleep -Seconds 20 }
}
if (-not $up) {
    Write-Host "App never came online - aborting." -ForegroundColor Yellow
    exit 1
}
"app is up; $(Get-Date -AsUTC -Format 'u')"

""
"=== HTTP/1.1 + HTTP/2 across the front end ==="
$paths = @("/api/v1/flights", "/graphql/schema.graphql", "/", "/soap/booking?wsdl")
foreach ($p in $paths) {
    foreach ($v in @([version]"1.1", [version]"2.0")) {
        try {
            $r = Invoke-WebRequest "$BaseUrl$p" -UseBasicParsing -HttpVersion $v -TimeoutSec 60
            $negotiated = $r.BaseResponse.RequestMessage.Version
            Result ("{0} on {1}" -f $p, $v) ($r.StatusCode -eq 200 -and $negotiated -eq $v) ("status $($r.StatusCode), negotiated $negotiated")
        }
        catch {
            Result ("{0} on {1}" -f $p, $v) $false $_.Exception.Message
        }
    }
}

""
"=== gRPC through the front end (GrpcProbe against $BaseUrl) ==="
$grpcUrl = $BaseUrl
$probeOut = $null
$grpcOk = $false
for ($i = 1; $i -le $MaxGrpcAttempts; $i++) {
    "attempt $i -> table"
    $probeOut = dotnet run --project tools/GrpcProbe -- $grpcUrl table 60 2>&1 | Out-String
    if ($LASTEXITCODE -eq 0 -and $probeOut -notmatch "Exception") { $grpcOk = $true; break }
    Write-Host "  no luck:" -ForegroundColor DarkYellow
    ($probeOut -split "`r?`n" | Where-Object { $_ -match "Status|Exception" } | Select-Object -First 2) | ForEach-Object { "  $_" }
    if ($i -lt $MaxGrpcAttempts) { Start-Sleep -Seconds 25 }
}
Result "gRPC unary (board)" $grpcOk ($probeOut.TrimEnd().Split("`n") | Select-Object -Last 3 -Join " | ")

if ($grpcOk) {
    $streamOut = dotnet run --project tools/GrpcProbe -- $grpcUrl watch 10 60 2>&1 | Out-String
    Result "gRPC server streaming (watch)" ($LASTEXITCODE -eq 0 -and $streamOut -notmatch "Exception") ($streamOut.TrimEnd().Split("`n") | Select-Object -Last 3 -Join " | ")

    $scanOut = dotnet run --project tools/GrpcProbe -- $grpcUrl scan 2>&1 | Out-String
    Result "gRPC bidirectional (bag scanner)" ($LASTEXITCODE -eq 0 -and $scanOut -notmatch "Exception") ($scanOut.TrimEnd().Split("`n") | Select-Object -Last 3 -Join " | ")
}

""
"=== streaming/WS regression through the front end (smoke-streaming) ==="
$smokeOut = pwsh -NoProfile -File tools/smoke-streaming.ps1 -BaseUrl $BaseUrl -GrpcProbeUrl $grpcUrl 2>&1 | Out-String
if ($smokeOut -match "(\d+) checks passed, (\d+) failed") {
    $p = [int]$Matches[1]; $f = [int]$Matches[2]
    Result "smoke-streaming ($p pass / $f fail)" ($f -eq 0) ($Matches.0)
}
else {
    $tail = $smokeOut.TrimEnd().Split("`n") | Select-Object -Last 8
    Result "smoke-streaming" $false ($tail -Join " | ")
}
# WebSocket verdict in full-h2-proxy mode:
$wsOk = $smokeOut -notmatch "FAIL.*WebSocket|FAIL.*subscription|FAIL.*ws "
Result "WebSocket upgrades survived proxy mode" $wsOk "if this fails while gRPC passes, set http20ProxyFlag=2 (gRPC Only) and re-run"

""
"=== diagnostics on gRPC failure (container stdout via Kudu) ==="
if (-not $grpcOk) {
    try {
        $prof = az webapp deployment list-publishing-profiles -g $ResourceGroup -n $WebAppName --xml 2>&1 | Out-String
        [xml]$xml = $prof
        $msd = $xml.publishData.publishProfile | Where-Object { $_.publishMethod -eq "MSDeploy" } | Select-Object -First 1
        $auth = "Basic " + [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("$($msd.userName):$($msd.userPWD)"))
        $scm = $BaseUrl -replace "\.azurewebsites\.net", ".scm.azurewebsites.net"
        $logs = Invoke-RestMethod -Uri "$scm/api/vfs/LogFiles" -Headers @{ Authorization = $auth } -TimeoutSec 60
        $latest = $logs | Where-Object { $_.name -match "\.log$" } | Sort-Object mtime -Descending | Select-Object -First 1
        if ($latest) {
            $raw = Invoke-WebRequest -Uri "$scm/api/vfs/LogFiles/$($latest.name)" -Headers @{ Authorization = $auth } -TimeoutSec 60 -UseBasicParsing
            $text = if ($raw.Content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($raw.Content) } else { [string]$raw.Content }
            "container log ($($latest.name)) - relevant lines:"
            ($text -split "`r?`n") | Where-Object { $_ -match "Now listening|Application started|Exception|5007|error" } |
                Select-Object -First 15 | ForEach-Object { "  $_" }
        }
        else { "no log files found in /home/LogFiles" }
    }
    catch { "Kudu log fetch failed: $($_.Exception.Message)" }
}

""
"=== summary ==="
"passed: $script:pass   failed: $script:fail"
if ($script:fail -gt 0) { exit 1 } else { exit 0 }

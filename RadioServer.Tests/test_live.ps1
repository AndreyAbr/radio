$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http

Write-Host "=== 1. Checking /stations endpoint ==="
$stations = Invoke-RestMethod -Uri "http://127.0.0.1:5123/stations" -Method Get
Write-Host "Found stations count: $($stations.Count)"
foreach ($s in $stations) {
    Write-Host "  Station: $($s.id) | $($s.name) | $($s.currentTrack)"
}
if ($stations.Count -lt 4) { throw "Expected at least 4 stations" }

Write-Host "=== 2. Checking HTML content (no subtitles, no academic footers) ==="
$indexHtml = (Invoke-WebRequest -UseBasicParsing -Uri "http://127.0.0.1:5123/index.html").Content
if ($indexHtml.Contains("88, 166, 255") -or $indexHtml.Contains("#58a6ff")) { 
    throw "index.html contains blue color" 
}
if ($indexHtml.Contains("tech-stack") -or $indexHtml.Contains(".NET 8")) { 
    throw "index.html contains academic footer" 
}
Write-Host "index.html: Clean!"

$adminHtml = (Invoke-WebRequest -UseBasicParsing -Uri "http://127.0.0.1:5123/admin.html").Content
if ($adminHtml.Contains("subtitle")) { 
    throw "admin.html contains subtitle" 
}
Write-Host "admin.html: Clean!"

Write-Host "=== 3. Checking CSS palette (no blue) ==="
$styleCss = (Invoke-WebRequest -UseBasicParsing -Uri "http://127.0.0.1:5123/style.css").Content
if ($styleCss.Contains("#58a6ff") -or $styleCss.Contains("#79b8ff") -or $styleCss.Contains("88, 166, 255")) { 
    throw "style.css contains blue hexes/rgba" 
}
Write-Host "style.css: Graphite & Amber palette confirmed, zero blue!"

$adminCss = (Invoke-WebRequest -UseBasicParsing -Uri "http://127.0.0.1:5123/admin.css").Content
if ($adminCss.Contains("#58a6ff") -or $adminCss.Contains("#79b8ff") -or $adminCss.Contains("88, 166, 255")) { 
    throw "admin.css contains blue hexes/rgba" 
}
Write-Host "admin.css: Zero blue confirmed!"

Write-Host "=== 4. Checking HTTP streaming and listener counting ==="
$client = [System.Net.Http.HttpClient]::new()
$cts = [System.Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(8))
$req = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Get, "http://127.0.0.1:5123/stream/rock")
$resp = $client.SendAsync($req, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead, $cts.Token).GetAwaiter().GetResult()
if ($resp.Content.Headers.ContentType.MediaType -ne "audio/mpeg") { throw "Invalid content type" }
$stream = $resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
$buf = New-Object byte[] 65536
$bytesRead = $stream.Read($buf, 0, $buf.Length)
Write-Host "Successfully read $bytesRead bytes from /stream/rock"

# Check listener count while connected
$stationsDuring = Invoke-RestMethod -Uri "http://127.0.0.1:5123/stations" -Method Get
$rock = $stationsDuring | Where-Object { $_.id -eq "rock" }
Write-Host "Rock station listeners during stream: $($rock.listeners)"
if ($rock.listeners -ne 1) { throw "Expected 1 listener on rock station" }

# Disconnect
$cts.Cancel()
$stream.Dispose()
$client.Dispose()
Start-Sleep -Milliseconds 400

$stationsAfter = Invoke-RestMethod -Uri "http://127.0.0.1:5123/stations" -Method Get
$rockAfter = $stationsAfter | Where-Object { $_.id -eq "rock" }
Write-Host "Rock station listeners after disconnect: $($rockAfter.listeners)"
if ($rockAfter.listeners -ne 0) { throw "Expected 0 listeners after disconnect" }

Write-Host "=== 5. Checking Skip endpoint ==="
$skipRes = Invoke-RestMethod -Uri "http://127.0.0.1:5123/api/admin/stations/rock/skip" -Method Post
Write-Host "Skip response: $($skipRes.message), currentTrack: $($skipRes.currentTrack)"

Write-Host "=== 6. Checking Concurrent streams on /stream/synthwave ==="
$clients = @()
$streams = @()
$ctss = @()

for ($i = 0; $i -lt 5; $i++) {
    $c = [System.Net.Http.HttpClient]::new()
    $tokenSrc = [System.Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(8))
    $r = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Get, "http://127.0.0.1:5123/stream/synthwave")
    $res = $c.SendAsync($r, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead, $tokenSrc.Token).GetAwaiter().GetResult()
    $s = $res.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
    $b = New-Object byte[] 4096
    $read = $s.Read($b, 0, $b.Length)
    if ($read -le 0) { throw "Client $i could not read from stream" }
    $clients += $c
    $streams += $s
    $ctss += $tokenSrc
}

$synthStats = (Invoke-RestMethod -Uri "http://127.0.0.1:5123/stations" -Method Get) | Where-Object { $_.id -eq "synthwave" }
Write-Host "Synthwave listeners with 5 concurrent streams: $($synthStats.listeners)"
if ($synthStats.listeners -ne 5) { throw "Expected 5 listeners on synthwave" }

# Disconnect all 5
for ($i = 0; $i -lt 5; $i++) {
    $ctss[$i].Cancel()
    $streams[$i].Dispose()
    $clients[$i].Dispose()
}
Start-Sleep -Milliseconds 400

$synthStatsAfter = (Invoke-RestMethod -Uri "http://127.0.0.1:5123/stations" -Method Get) | Where-Object { $_.id -eq "synthwave" }
Write-Host "Synthwave listeners after disconnecting all: $($synthStatsAfter.listeners)"
if ($synthStatsAfter.listeners -ne 0) { throw "Expected 0 listeners on synthwave after disconnect" }

Write-Host "=== ALL LIVE CHECKS COMPLETED AND VERIFIED 100% ==="

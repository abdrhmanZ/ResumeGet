#requires -Version 7.0
[CmdletBinding()]
param(
    [string] $CliPath,
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$projectRoot = Split-Path -Parent $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($CliPath)) {
    $project = Join-Path $projectRoot 'src/ResumeGet.Cli/ResumeGet.Cli.csproj'
    & dotnet build $project --configuration Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'CLI build failed. The demo was not started.' }
    $CliPath = Join-Path $projectRoot 'src/ResumeGet.Cli/bin/Release/net10.0/resumeget.dll'
}
$CliPath = (Resolve-Path -LiteralPath $CliPath).Path
if ([IO.Path]::GetExtension($CliPath) -ne '.dll') {
    throw '-CliPath must point to resumeget.dll, alongside its runtime files.'
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path ([IO.Path]::GetTempPath()) ('ResumeGet-demo-' + [Guid]::NewGuid().ToString('N'))
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) {
    throw 'Choose a new output directory. The demo never reuses or deletes an existing directory.'
}
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$expectedPath = Join-Path $OutputDirectory 'expected.bin'
$downloadPath = Join-Path $OutputDirectory 'download.bin'
$payload = [byte[]]::new(512 * 1024)
[Random]::new(42).NextBytes($payload)
[IO.File]::WriteAllBytes($expectedPath, $payload)
$prefixLength = 128 * 1024

$serverJob = Start-Job -ArgumentList $expectedPath, $prefixLength -ScriptBlock {
    param($ExpectedPath, $PrefixLength)
    $ErrorActionPreference = 'Stop'
    $payload = [IO.File]::ReadAllBytes($ExpectedPath)
    $etag = '"demo-v1"'
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = $listener.LocalEndpoint.Port
    [pscustomobject]@{ Kind = 'ready'; Url = "http://127.0.0.1:$port/sample.bin" }
    try {
        for ($attempt = 1; $attempt -le 2; $attempt++) {
            $accept = $listener.AcceptTcpClientAsync()
            if (-not $accept.Wait(30000)) { throw 'Timed out waiting for the CLI request.' }
            $connection = $accept.GetAwaiter().GetResult()
            try {
                $stream = $connection.GetStream()
                $stream.ReadTimeout = 10000
                $stream.WriteTimeout = 10000
                $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::ASCII, $false, 1024, $true)
                try {
                    $requestLine = $reader.ReadLine()
                    if ($requestLine -notmatch '^GET /sample\.bin HTTP/') { throw 'Unexpected HTTP request.' }
                    $headers = @{}
                    while (-not [string]::IsNullOrEmpty(($line = $reader.ReadLine()))) {
                        $separator = $line.IndexOf(':')
                        $headers[$line.Substring(0, $separator)] = $line.Substring($separator + 1).Trim()
                    }
                }
                finally { $reader.Dispose() }

                if ($attempt -eq 1) {
                    $status = '200 OK'
                    $offset = 0
                    $bodyLength = $PrefixLength
                    $rangeHeader = ''
                    $advertisedLength = $payload.Length
                }
                else {
                    if ($headers['Range'] -ne "bytes=$PrefixLength-" -or $headers['If-Range'] -ne $etag) {
                        throw 'The second request did not contain the expected Range and If-Range headers.'
                    }
                    $status = '206 Partial Content'
                    $offset = $PrefixLength
                    $bodyLength = $payload.Length - $offset
                    $advertisedLength = $bodyLength
                    $rangeHeader = "Content-Range: bytes $offset-$($payload.Length - 1)/$($payload.Length)`r`n"
                }
                $response = "HTTP/1.1 $status`r`nConnection: close`r`nETag: $etag`r`n" +
                    "Content-Length: $advertisedLength`r`n$rangeHeader`r`n"
                $headerBytes = [Text.Encoding]::ASCII.GetBytes($response)
                $stream.Write($headerBytes, 0, $headerBytes.Length)
                $stream.Write($payload, $offset, $bodyLength)
                $stream.Flush()
                [pscustomobject]@{ Kind = 'transfer'; Message = "Request ${attempt}: $status, $bodyLength bytes sent." }
                # Closing the first response early creates a real interrupted HTTP transfer.
            }
            finally { $connection.Dispose() }
        }
    }
    finally { $listener.Stop() }
}

try {
    $ready = $null
    $startup = [Diagnostics.Stopwatch]::StartNew()
    while ($null -eq $ready -and $startup.Elapsed.TotalSeconds -lt 15) {
        foreach ($notice in @(Receive-Job -Job $serverJob)) {
            if ($notice.Kind -eq 'ready') { $ready = $notice }
        }
        if ($serverJob.State -in @('Failed', 'Stopped', 'Completed')) { break }
        if ($null -eq $ready) { Start-Sleep -Milliseconds 100 }
    }
    if ($null -eq $ready) { throw 'The local demo server did not become ready.' }

    Write-Host "`n1 / Download until the local server cuts the connection." -ForegroundColor Cyan
    & dotnet $CliPath $ready.Url --output $downloadPath
    $firstExit = $LASTEXITCODE
    if ($firstExit -eq 0 -or -not (Test-Path -LiteralPath ($downloadPath + '.part'))) {
        throw 'The first run must fail with a saved partial file. Check the CLI output above.'
    }
    $savedLength = (Get-Item -LiteralPath ($downloadPath + '.part')).Length
    if ($savedLength -ne $prefixLength -or (Test-Path -LiteralPath $downloadPath)) {
        throw 'The interrupted download did not preserve the expected prefix.'
    }

    Write-Host "`n2 / Run the same command again; resume from $savedLength bytes." -ForegroundColor Cyan
    & dotnet $CliPath $ready.Url --output $downloadPath
    if ($LASTEXITCODE -ne 0) { throw 'The second run failed to resume.' }
    Wait-Job -Job $serverJob -Timeout 10 | Out-Null
    foreach ($notice in @(Receive-Job -Job $serverJob)) {
        if ($notice.Kind -eq 'transfer') { Write-Host $notice.Message }
    }
    if ($serverJob.State -ne 'Completed') { throw 'The local demo server did not finish correctly.' }

    $expectedHash = (Get-FileHash -LiteralPath $expectedPath -Algorithm SHA256).Hash
    $actualHash = (Get-FileHash -LiteralPath $downloadPath -Algorithm SHA256).Hash
    if ($expectedHash -ne $actualHash) { throw 'The downloaded bytes do not match the original file.' }
    Write-Host "`nPASS / Interrupted, resumed, and SHA-256 matched." -ForegroundColor Green
    Write-Host "SHA-256: $actualHash"
    Write-Host "Files kept at: $OutputDirectory"
}
finally {
    Stop-Job -Job $serverJob
    Remove-Job -Job $serverJob
}

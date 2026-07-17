$ErrorActionPreference = 'Stop'
$gameRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$exe = Join-Path $gameRoot 'A Township Tale.exe'
$melonLogs = Join-Path $gameRoot 'MelonLoader\Logs'
$runStarted = Get-Date
$processes = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()

$existingGame = Get-Process -Name 'A Township Tale' -ErrorAction SilentlyContinue
if ($existingGame) { throw 'A Township Tale is already running; refusing to interfere with existing processes' }
if (netstat -ano | Select-String -Pattern ':1757\s') { throw 'Port 1757 is already in use' }

function Get-LauncherArguments([string]$name) {
    $line = Get-Content -LiteralPath (Join-Path $gameRoot $name) |
        Where-Object { $_ -match '^start\s+""\s+"\./A Township Tale\.exe"\s+' } |
        Select-Object -Last 1
    if (-not $line) { throw "Could not parse $name" }
    return ($line -replace '^start\s+""\s+"\./A Township Tale\.exe"\s+', '')
}

function ConvertFrom-Base64Url([string]$value) {
    $value = $value.Replace('-', '+').Replace('_', '/')
    while (($value.Length % 4) -ne 0) { $value += '=' }
    return [Convert]::FromBase64String($value)
}

function ConvertTo-Base64Url([byte[]]$value) {
    return [Convert]::ToBase64String($value).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

function Set-JwtIdentity([string]$token, [int]$userId, [string]$username) {
    $segments = $token.Split('.')
    if ($segments.Length -ne 3) { throw 'Unexpected JWT format' }
    $json = [Text.Encoding]::UTF8.GetString((ConvertFrom-Base64Url $segments[1]))
    $payload = $json | ConvertFrom-Json
    if ($payload.PSObject.Properties['UserId']) { $payload.UserId = "$userId" }
    if ($payload.PSObject.Properties['Username']) { $payload.Username = $username }
    $rewritten = $payload | ConvertTo-Json -Compress -Depth 20
    $segments[1] = ConvertTo-Base64Url ([Text.Encoding]::UTF8.GetBytes($rewritten))
    return $segments -join '.'
}

function Set-ClientIdentity([string]$arguments, [int]$userId, [string]$username) {
    $parts = [System.Collections.Generic.List[string]]($arguments -split '\s+')
    foreach ($tokenName in @('/access_token', '/refresh_token', '/identity_token')) {
        $index = $parts.IndexOf($tokenName)
        if ($index -lt 0) { throw "Missing $tokenName" }
        $parts[$index + 1] = Set-JwtIdentity $parts[$index + 1] $userId $username
    }
    return $parts -join ' '
}

function Start-TestProcess([string]$arguments) {
    $process = Start-Process -FilePath $exe -ArgumentList $arguments -WindowStyle Hidden -PassThru
    $processes.Add($process)
    return $process
}

function Get-TestMarkers {
    $logs = Get-ChildItem -LiteralPath $melonLogs -File |
        Where-Object { $_.LastWriteTime -ge $runStarted }
    if (-not $logs) { return @() }
    return $logs | Select-String -Pattern 'VOIP_TEST|Voice channel|Initialized; Vivox|Concentus encode/decode|Disconnected for|ModAudio|bank load' |
        ForEach-Object { "[$($_.Path | Split-Path -Leaf)] $($_.Line)" }
}

try {
    $serverArgs = (Get-LauncherArguments 'startServer.bat') + ' /voip_test_range -logFile "voip-test-server.log"'
    $clientArgs = Get-LauncherArguments 'startClient.bat'
    $receiverArgs = $clientArgs + ' /voip_test_receiver /voip_test_expect_sender 2 /voip_test_gui -logFile "voip-test-receiver.log"'
    $senderArgs = (Set-ClientIdentity $clientArgs 2 'VoipSender') + ' /voip_test_sender -logFile "voip-test-sender.log"'

    $server = Start-TestProcess $serverArgs
    $deadline = (Get-Date).AddSeconds(45)
    do {
        Start-Sleep -Milliseconds 500
        if ($server.HasExited) { throw "Server exited with code $($server.ExitCode)" }
        $listening = netstat -ano | Select-String -Pattern ':1757\s'
    } until ($listening -or (Get-Date) -ge $deadline)
    if (-not $listening) { throw 'Server did not listen on port 1757' }

    $receiver = Start-TestProcess $receiverArgs
    Start-Sleep -Seconds 12
    if ($receiver.HasExited) { throw "Receiver exited with code $($receiver.ExitCode)" }
    $sender = Start-TestProcess $senderArgs

    $deadline = (Get-Date).AddSeconds(75)
    do {
        Start-Sleep -Seconds 1
        if ($sender.HasExited) { throw "Sender exited with code $($sender.ExitCode)" }
        if ($receiver.HasExited) { throw "Receiver exited with code $($receiver.ExitCode)" }
        $markerText = (Get-TestMarkers) -join "`n"
        $finished = $markerText -match 'SEND_COMPLETE' -and
            $markerText -match 'REMOTE_SOURCE_PLAYING_OK' -and
            $markerText -match 'SUSTAINED ' -and
            $markerText -match 'DECODE_OK' -and
            $markerText -match 'RECOVERY_OK.+fec=1.+plc=[34]' -and
            $markerText -match 'RANGE_DROP.+seq=5' -and
            $markerText -match 'RELAY.+seq=14' -and
            $markerText -match 'VIOLATION.+count=3'
    } until ($finished -or (Get-Date) -ge $deadline)

    $markers = Get-TestMarkers
    $markers
    if (-not $finished) { throw 'VOIP integration markers did not complete before timeout' }

    $receivedSequences = @($markers | Where-Object { $_ -match 'RECEIVED.+seq=(\d+)' } | ForEach-Object { [int]$Matches[1] })
    $unexpected = @($receivedSequences | Where-Object { $_ -ge 5 -and $_ -lt 10 })
    if ($unexpected.Count -ne 0) { throw "Out-of-range packets reached receiver: $($unexpected -join ',')" }
    foreach ($required in @(0, 4, 10, 14)) {
        if ($required -notin $receivedSequences) { throw "Receiver did not get required sequence $required" }
    }
    $sustainedLine = $markers | Where-Object { $_ -match 'SUSTAINED plc=(\d+) fec=(\d+) late=(\d+) waits=(\d+) latencyDrops=(\d+).+bufferMs=(\d+)' } | Select-Object -Last 1
    if (-not $sustainedLine) { throw 'Missing sustained-stream metrics' }
    [void]($sustainedLine -match 'SUSTAINED plc=(\d+) fec=(\d+) late=(\d+) waits=(\d+) latencyDrops=(\d+).+bufferMs=(\d+)')
    if ([int]$Matches[1] -gt 7) { throw "Excessive PLC during sustained three-process stream: $($Matches[1])" }
    if ([int]$Matches[3] -gt 4) { throw "Too many late packets during sustained three-process stream: $($Matches[3])" }
    if ([int]$Matches[5] -ne 0) { throw "Latency control dropped frames during sustained local stream: $($Matches[5])" }
    if ([int]$Matches[6] -gt 700) { throw "Jitter buffer exceeded its 64-packet safety bound: $($Matches[6]) ms" }

    # abrupt client shutdown used to make the windows udp socket throw WSAECONNRESET every tick, keep server alive to prove the socket patch stops it
    Stop-Process -Id $sender.Id -Force
    Stop-Process -Id $receiver.Id -Force
    Start-Sleep -Seconds 3
    $transportSpam = Get-ChildItem -LiteralPath $melonLogs -File |
        Where-Object { $_.LastWriteTime -ge $runStarted } |
        Select-String -Pattern 'looks like the other end has closed the connection'
    if ($transportSpam) { throw 'KCP UDP connection-reset log spam returned after forced client shutdown' }
    'VOIP_TEST KCP_FORCED_CLOSE=PASS'
    'VOIP_TEST RESULT=PASS'
}
finally {
    foreach ($process in $processes) {
        if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
    }
}

# PC side of the Phase 0 Android spike: connects over Wi-Fi, sends commands, prints phone events.
# -AutoAnswer: answer from the PC when the phone rings, then hang up after -HoldSeconds.
param(
    [Parameter(Mandatory)] [string] $Ip,
    [Parameter(Mandatory)] [string] $Token,
    [string[]] $Commands = @('STATUS', 'SMS', 'PHOTOS', 'NOTIF', 'MEDIA', 'CLIP Palwyn clipboard test'),
    [int] $ListenSeconds = 5,
    [switch] $AutoAnswer,
    [int] $HoldSeconds = 8,
    [int] $PingSeconds = 0
)
$ErrorActionPreference = 'Stop'
$client = [System.Net.Sockets.TcpClient]::new()
$sw = [Diagnostics.Stopwatch]::StartNew()
$client.Connect($Ip, 47800)
"connected in $($sw.ElapsedMilliseconds) ms"
$stream = $client.GetStream()
$reader = [IO.StreamReader]::new($stream)
$writer = [IO.StreamWriter]::new($stream); $writer.AutoFlush = $true
$writer.WriteLine($Token)

$script:pending = $reader.ReadLineAsync()
function Read-Lines([int] $ms) {
    $until = [DateTime]::UtcNow.AddMilliseconds($ms)
    $out = @()
    while ([DateTime]::UtcNow -lt $until) {
        if ($script:pending.Wait(50)) {
            if ($null -eq $script:pending.Result) { $script:closed = $true; break }
            $out += $script:pending.Result
            $script:pending = $reader.ReadLineAsync()
        }
    }
    $out
}

foreach ($c in $Commands) {
    $writer.WriteLine($c)
    Read-Lines 1500 | ForEach-Object { "$([DateTime]::Now.ToString('HH:mm:ss.fff')) $_" }
}

$end = [DateTime]::UtcNow.AddSeconds($ListenSeconds)
$answeredAt = $null
$nextPing = [DateTime]::UtcNow
while ([DateTime]::UtcNow -lt $end -and -not $script:closed) {
    if ($PingSeconds -gt 0 -and [DateTime]::UtcNow -ge $nextPing) {
        $writer.WriteLine('PING'); $nextPing = [DateTime]::UtcNow.AddSeconds($PingSeconds)
    }
    foreach ($l in (Read-Lines 200)) {
        if ($l -like 'RES UNKNOWN*') { continue }
        $age = if ($l -match 't=(\d+)') { [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() - [long]$Matches[1] } else { 0 }
        "$([DateTime]::Now.ToString('HH:mm:ss.fff')) [delay ${age} ms] $l"
        if ($AutoAnswer -and -not $answeredAt -and $l -match 'PHONE_STATE state=RINGING' -and $age -lt 5000) {
            Start-Sleep -Seconds 2
            $writer.WriteLine('ANSWER'); $answeredAt = [DateTime]::UtcNow
            "$([DateTime]::Now.ToString('HH:mm:ss.fff')) >>> ANSWER sent from PC"
        }
    }
    if ($answeredAt -and ([DateTime]::UtcNow - $answeredAt).TotalSeconds -ge $HoldSeconds) {
        $writer.WriteLine('END'); $answeredAt = [DateTime]::MaxValue.AddDays(-1)
        "$([DateTime]::Now.ToString('HH:mm:ss.fff')) >>> END sent from PC"
    }
}
if ($script:closed) { "connection closed by phone" }
$client.Close()

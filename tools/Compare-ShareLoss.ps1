<#
.SYNOPSIS
    Groups outbound-video health samples by the encoder configuration in force at the
    time, so two share settings can be compared on equal footing.

.DESCRIPTION
    Every five seconds the media engine logs one "Outbound video" line carrying the frame
    rate, the bitrate actually sent, and the congestion controller's reason - which names
    the loss percentage when there is any. Separately it logs "Video encoder:" whenever an
    encoder is built, which is where the resolution and frame rate come from.

    Reading the two together attributes each sample to a configuration, which is what makes
    an interleaved A/B trustworthy: a residential link varies minute to minute, so
    comparing one run against another run half an hour later measures the link as much as
    the setting. Alternating the settings and grouping afterwards cancels that out.

.EXAMPLE
    .\Compare-ShareLoss.ps1 -SinceMinutes 30
#>
[CmdletBinding()]
param(
    [string] $Log = "$env:APPDATA\ChatFlow\logs\chatflow.log",

    # Only consider samples from the last N minutes, so old sessions do not skew the result.
    [int] $SinceMinutes = 60,

    # Print every sample as well as the summary.
    [switch] $Detailed
)

if (-not (Test-Path $Log)) { Write-Error "Log not found: $Log"; exit 1 }

$cutoff = (Get-Date).AddMinutes(-$SinceMinutes).TimeOfDay
$current = 'unknown'
$rows = New-Object System.Collections.Generic.List[object]

foreach ($line in Get-Content $Log) {
    # The codec probe builds throwaway 320x240 encoders when the settings dialog opens;
    # they are not a share and must not relabel the samples that follow.
    if ($line -match 'Video encoder: \S+ \(\w+ (\d+)x(\d+)@(\d+)') {
        if ("$($matches[1])x$($matches[2])" -ne '320x240') {
            $current = "$($matches[1])x$($matches[2])@$($matches[3])"
        }
        continue
    }

    if ($line -notmatch '^(\d\d:\d\d:\d\d).*Outbound video: (\d+) fps captured.*?(\d+) kbps \(') { continue }

    $time = $matches[1]
    $fps = [int]$matches[2]
    $kbps = [int]$matches[3]

    if ([TimeSpan]::Parse($time) -lt $cutoff) { continue }

    # "18.0 % loss" / "3.5 % loss, holding"; absent means the interval was clean.
    $loss = 0.0
    if ($line -match '([\d.]+) % loss') { $loss = [double]$matches[1] }

    # A window with nothing flowing says nothing about the link.
    if ($kbps -le 0) { continue }

    $rows.Add([pscustomobject]@{
        Time   = $time
        Config = $current
        Fps    = $fps
        Kbps   = $kbps
        Loss   = $loss
    })
}

if ($rows.Count -eq 0) { Write-Warning "No samples in the last $SinceMinutes minute(s)."; exit 0 }

if ($Detailed) {
    $rows | Format-Table -AutoSize
    ''
}

'{0,-16} {1,7} {2,9} {3,9} {4,9} {5,9} {6,9}' -f 'CONFIG', 'SAMPLES', 'MEAN', 'MEDIAN', 'WORST', 'LOSSY%', 'MEAN'
'{0,-16} {1,7} {2,9} {3,9} {4,9} {5,9} {6,9}' -f '', '', 'LOSS', 'LOSS', 'LOSS', 'WINDOWS', 'KBPS'
'-' * 78

foreach ($group in $rows | Group-Object Config | Sort-Object Name) {
    $losses = $group.Group | ForEach-Object { $_.Loss }
    $sorted = $losses | Sort-Object

    # A window is "lossy" past 2%, the point the controller stops growing.
    $lossy = ($losses | Where-Object { $_ -gt 2 }).Count

    '{0,-16} {1,7} {2,8:N1}% {3,8:N1}% {4,8:N1}% {5,8:N0}% {6,9:N0}' -f `
        $group.Name,
        $group.Count,
        ($losses | Measure-Object -Average).Average,
        $sorted[[int]($sorted.Count / 2)],
        ($losses | Measure-Object -Maximum).Maximum,
        ($lossy * 100 / $group.Count),
        ($group.Group | Measure-Object Kbps -Average).Average
}

''
'Interleave the settings - two minutes each, alternating, three rounds - or this compares'
'link conditions as much as it compares settings.'

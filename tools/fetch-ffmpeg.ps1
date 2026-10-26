<#
.SYNOPSIS
    Downloads the FFmpeg shared libraries the media pipeline needs.

.DESCRIPTION
    FFmpeg.AutoGen ships bindings only. This fetches a matching SHARED build and stages
    the DLLs into third_party/ffmpeg/<rid>/, where ChatApp.Media.csproj picks them up and
    copies them to runtimes/<rid>/native/ in the build output.

    A STATIC FFmpeg build (what `winget install Gyan.FFmpeg` gives you) will NOT work:
    it contains ffmpeg.exe and no DLLs.

    LICENSING: the default build is GPL, because software H.264/H.265 encoding needs
    libx264/libx265 which are GPL-only. Distributing the app linked against it carries
    GPL obligations. Pass -Lgpl for an LGPL build instead — but note it has no x264/x265,
    so software H.264 encoding will be unavailable and only hardware encoders
    (NVENC/QSV/AMF/MediaFoundation) will work.

.PARAMETER Lgpl
    Fetch the LGPL build instead of GPL. See the licensing note above.

.PARAMETER Force
    Re-download even if the DLLs are already staged.
#>
[CmdletBinding()]
param(
    [switch]$Lgpl,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

# Must match the FFmpeg major/minor that FFmpeg.AutoGen binds (currently 8.1 ->
# avcodec-62, avutil-60, avformat-62, swscale-9, swresample-6).
$branch  = 'n8.1'
$variant = if ($Lgpl) { 'lgpl' } else { 'gpl' }
$asset   = "ffmpeg-$branch-latest-win64-$variant-shared-8.1.zip"
$url     = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/$asset"

$repoRoot = Split-Path -Parent $PSScriptRoot
$destDir  = Join-Path $repoRoot 'third_party\ffmpeg\win-x64'

# avdevice and avfilter are required for capture:
#   avdevice -> dshow (camera) and the lavfi input device
#   avfilter -> ddagrab, the DXGI Desktop Duplication screen grabber, which is a filter
$needed = @(
    'avcodec-62.dll',
    'avutil-60.dll',
    'avformat-62.dll',
    'avdevice-62.dll',
    'avfilter-11.dll',
    'swscale-9.dll',
    'swresample-6.dll'
)

$alreadyStaged = (Test-Path $destDir) -and
                 (@($needed | Where-Object { -not (Test-Path (Join-Path $destDir $_)) }).Count -eq 0)

if ($alreadyStaged -and -not $Force) {
    Write-Output "FFmpeg already staged in $destDir. Use -Force to re-download."
    exit 0
}

$work = Join-Path ([System.IO.Path]::GetTempPath()) ("ffmpeg-fetch-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $work | Out-Null

try {
    $zipPath = Join-Path $work 'ffmpeg.zip'

    Write-Output "Downloading $asset ..."
    Invoke-WebRequest -Uri $url -OutFile $zipPath -UseBasicParsing

    $sizeMb = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
    Write-Output "Downloaded $sizeMb MB. Extracting ..."

    Expand-Archive -Path $zipPath -DestinationPath (Join-Path $work 'extracted') -Force

    $binDir = Get-ChildItem -Path (Join-Path $work 'extracted') -Directory |
              Select-Object -First 1 |
              ForEach-Object { Join-Path $_.FullName 'bin' }

    if (-not (Test-Path $binDir)) {
        throw "Could not find a bin directory inside $asset."
    }

    New-Item -ItemType Directory -Force -Path $destDir | Out-Null

    foreach ($dll in $needed) {
        $source = Join-Path $binDir $dll
        if (-not (Test-Path $source)) {
            throw "$dll is missing from the archive. FFmpeg.AutoGen's expected ABI may have changed."
        }
        Copy-Item $source -Destination $destDir -Force
    }

    $license = Get-ChildItem -Path (Join-Path $work 'extracted') -Recurse -Filter 'LICENSE*' |
               Select-Object -First 1
    if ($license) {
        Copy-Item $license.FullName -Destination (Join-Path $destDir 'LICENSE.txt') -Force
    }

    $totalMb = [math]::Round((Get-ChildItem $destDir -File | Measure-Object Length -Sum).Sum / 1MB, 1)
    Write-Output "Staged $totalMb MB of FFmpeg ($variant) into $destDir"
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}

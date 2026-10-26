<#
.SYNOPSIS
    Builds a ChatFlow release, stamps the version everywhere it appears, and
    deploys it over SSH.

.DESCRIPTION
    One release involves two machines:

      * the download host - serves ChatLast<version>.zip / .apk
      * the server host   - runs ChatServer (linux-arm64) behind systemd

    The server is also what tells clients an update exists: UpdateController reads
    the AppUpdate section of appsettings.json and hands it to the app's
    UpdateService. So the version stamped into appsettings.json, the version in
    the artifact filenames, and the version compiled into the client all have to
    agree, or clients will either miss the update or download a 404. Keeping those
    in step is the main job of this script.

    Order matters and is deliberate: artifacts are uploaded to the download host
    BEFORE the server is told about them, so clients never see an announcement for
    a file that is not there yet.

    Remote appsettings.json is patched in place (AppUpdate section only) rather
    than overwritten, so production secrets on the server survive a deploy. Use
    -SettingsMode Replace to push the repo copy wholesale instead.

    Authentication is by SSH key (OpenSSH) or by password (PuTTY plink/pscp,
    since OpenSSH will not accept a password from a script). Passwords are
    prompted for at run time, handed to plink through a locked-down temp file
    rather than the command line, and deleted afterwards. They are never read
    from or written to the settings file.

.EXAMPLE
    ./tools/publish-release.ps1 -Version 1.9.0 -ReleaseNotes "Screen annotations" -UsePassword

    Full release, prompting for the SSH password of each host.

.EXAMPLE
    ./tools/publish-release.ps1 -Version 1.9.0 -ReleaseNotes "Screen annotations"

    Same, using SSH keys. Host details come from tools/publish-release.settings.json.

.EXAMPLE
    ./tools/publish-release.ps1 -Version 1.9.0 -Artifacts windows -DryRun

    Builds and stamps only; prints every remote command instead of running it.

.EXAMPLE
    ./tools/publish-release.ps1 -Version 1.9.0 -SkipDeploy

    Produces artifacts/release locally and stops.
#>
[CmdletBinding()]
param(
    # Release version, e.g. 1.9.0. Omit to reuse the version already in the csproj.
    [string]$Version,

    [string]$ReleaseNotes,

    # Which ChatAppBlazor artifacts to build.
    [ValidateSet('windows', 'android')]
    [string[]]$Artifacts = @('windows', 'android'),

    # SSH target serving the download files, e.g. deploy@www.duncan.com.ar
    [string]$DownloadHost,

    # Remote directory the download files are served from.
    [string]$DownloadPath,

    # Public base URL those files resolve to, e.g. https://www.duncan.com.ar
    [string]$DownloadBaseUrl,

    # SSH target running ChatServer, e.g. deploy@chat.duncan.com.ar
    [string]$ServerHost,

    # Remote directory ChatServer is deployed to.
    [string]$ServerPath,

    # systemd unit to restart after deploying.
    [string]$ServiceName,

    # Overrides how the server process is restarted. Needed on hosts where the
    # deploy account cannot sudo (CloudPanel site users, most shared hosting).
    # Examples:
    #   "sudo -n systemctl restart chatapp"      (default, needs NOPASSWD)
    #   "supervisorctl restart chatapp"
    #   "systemctl --user restart chatapp"
    # Set to "none" to deploy files only and restart yourself.
    [string]$RestartCommand,

    # Force password authentication. Not usually needed: password auth is the
    # default whenever no SshKey is configured. You are prompted once per host,
    # and passwords are never read from or written to the settings file.
    [switch]$UsePassword,

    # Force key/agent authentication even with no SshKey set (uses your default
    # OpenSSH identity or ssh-agent).
    [switch]$UseKey,

    # Supply the password non-interactively, e.g. from a secret store:
    #   -SshPassword (Read-Host -AsSecureString)
    # Only use this when both hosts share one credential.
    [securestring]$SshPassword,

    # Private key used for both hosts. Setting this selects key authentication.
    [string]$SshKey,

    # Allow the first connection to each host to prompt for host key confirmation.
    # PuTTY shows the fingerprint and waits for y/n - check it before accepting.
    [switch]$AcceptHostKeys,

    # Merge patches only AppUpdate on the remote; Replace uploads the repo file.
    [ValidateSet('Merge', 'Replace')]
    [string]$SettingsMode = 'Merge',

    # Build and stamp, but print remote commands instead of executing them.
    [switch]$DryRun,

    # Skip building; deploy whatever is already in artifacts/release.
    [switch]$SkipBuild,

    # Build only - no upload, no restart.
    [switch]$SkipDeploy
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$RepoRoot = Split-Path -Parent $PSScriptRoot
$OutputDir = Join-Path $RepoRoot 'artifacts/release'
$SettingsFile = Join-Path $PSScriptRoot 'publish-release.settings.json'

$BlazorProject = Join-Path $RepoRoot 'ChatAppBlazor/ChatAppBlazor.csproj'
$ServerProject = Join-Path $RepoRoot 'ChatServer/ChatServer.csproj'
$ServerSettings = Join-Path $RepoRoot 'ChatServer/appsettings.json'
$PatchScript = Join-Path $PSScriptRoot 'patch-appupdate.py'

$WindowsTfm = 'net11.0-windows10.0.19041.0'
$AndroidTfm = 'net11.0-android'
$ServerRid = 'linux-arm64'

# --- Output helpers ------------------------------------------------------------

function Write-Step { param([string]$Message) Write-Host "`n==> $Message" -ForegroundColor Cyan }
function Write-Info { param([string]$Message) Write-Host "    $Message" -ForegroundColor Gray }
function Write-Ok { param([string]$Message) Write-Host "    $Message" -ForegroundColor Green }
function Write-Warn { param([string]$Message) Write-Host "    $Message" -ForegroundColor Yellow }

function Invoke-Native {
    param(
        [Parameter(Mandatory)][string]$Exe,
        [Parameter(Mandatory)][string[]]$Arguments,
        [string]$What
    )

    $label = if ($What) { $What } else { "$Exe $($Arguments -join ' ')" }
    Write-Info $label

    # Out-Host, not bare invocation: a native command's stdout would otherwise
    # join this function's output, and any caller that returns a value after
    # calling it (Build-Server) would hand back build log lines instead.
    & $Exe @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "$label failed with exit code $LASTEXITCODE"
    }
}

# --- Settings ------------------------------------------------------------------

function Merge-Settings {
    <#
        Fills unset parameters from tools/publish-release.settings.json so host
        details need not be retyped (or committed). Explicit parameters win.
    #>
    if (-not (Test-Path $SettingsFile)) {
        return
    }

    Write-Info "Reading defaults from $(Split-Path -Leaf $SettingsFile)"
    $settings = Get-Content $SettingsFile -Raw | ConvertFrom-Json

    foreach ($name in @(
            'DownloadHost', 'DownloadPath', 'DownloadBaseUrl',
            'ServerHost', 'ServerPath', 'ServiceName', 'SshKey')) {

        $current = Get-Variable -Name $name -Scope Script -ValueOnly -ErrorAction SilentlyContinue
        if (-not [string]::IsNullOrWhiteSpace($current)) {
            continue
        }

        if ($settings.PSObject.Properties.Name -contains $name) {
            Set-Variable -Name $name -Scope Script -Value ([string]$settings.$name)
        }
    }
}

function Get-RestartCommand {
    <#
        -n makes sudo fail immediately instead of trying to read a password from a
        terminal that plink does not provide. Without it the deploy hangs or dies
        with "a terminal is required to read the password".
    #>
    if (-not [string]::IsNullOrWhiteSpace($RestartCommand)) {
        return $RestartCommand.Trim()
    }
    return "sudo -n systemctl restart '$ServiceName'"
}

function Test-RestartDisabled {
    return ($RestartCommand -and $RestartCommand.Trim().ToLowerInvariant() -eq 'none')
}

function Assert-DeploySettings {
    # DownloadBaseUrl is intentionally absent: it is derived from the existing
    # AppUpdate.DownloadUrl when not supplied.
    $missing = @()
    foreach ($name in @('DownloadHost', 'DownloadPath', 'ServerHost', 'ServerPath', 'ServiceName')) {
        $value = Get-Variable -Name $name -Scope Script -ValueOnly -ErrorAction SilentlyContinue
        if ([string]::IsNullOrWhiteSpace($value)) { $missing += $name }
    }

    if ($missing.Count -eq 0) {
        return
    }

    throw @"
Missing deploy settings: $($missing -join ', ')

Pass them as parameters, or create ${SettingsFile}:

{
  "DownloadHost":    "deploy@www.duncan.com.ar",
  "DownloadPath":    "/var/www/downloads",
  "DownloadBaseUrl": "https://www.duncan.com.ar",
  "ServerHost":      "deploy@chat.duncan.com.ar",
  "ServerPath":      "/opt/chatflow",
  "ServiceName":     "chatflow",
  "SshKey":          ""
}
"@
}

# --- SSH plumbing --------------------------------------------------------------
#
# Two transports:
#   key auth      -> OpenSSH ssh/scp (Windows ships these)
#   password auth -> PuTTY plink/pscp
#
# OpenSSH deliberately refuses to take a password from anything but a terminal,
# so password auth needs plink. Both plink and pscp support -pwfile, which reads
# the password from a file instead of the command line - command-line arguments
# are visible to every process on the machine, so -pw is avoided entirely.

$script:PasswordFiles = @{}
$script:HostKeysConfirmed = @{}

function Test-UsePasswordAuth {
    <#
        Password unless a key was actually selected. Requiring an explicit
        -UsePassword was a trap: forgetting it silently fell back to OpenSSH,
        which cannot prompt for a password under BatchMode and died with an
        unrelated "Host key verification failed."
    #>
    if ($UsePassword -or $null -ne $SshPassword) { return $true }
    if ($UseKey) { return $false }
    return [string]::IsNullOrWhiteSpace($SshKey)
}

function Get-AuthDescription {
    if (Test-UsePasswordAuth) {
        return 'password (PuTTY plink/pscp, prompted per host)'
    }
    if (-not [string]::IsNullOrWhiteSpace($SshKey)) {
        return "key $SshKey (OpenSSH)"
    }
    return 'key/agent (OpenSSH default identity)'
}

function Get-PuttyTool {
    param([Parameter(Mandatory)][ValidateSet('plink', 'pscp')][string]$Name)

    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    $fallback = Join-Path ${env:ProgramFiles} "PuTTY/$Name.exe"
    if (Test-Path $fallback) { return $fallback }

    throw @"
$Name not found, and password authentication needs it.

Install PuTTY (https://www.putty.org/) or add it to PATH, or switch to key
authentication with -SshKey <path>.
"@
}

function New-PasswordFile {
    <#
        Materializes a password into a temp file locked down to the current user,
        for plink/pscp -pwfile. Deleted by Remove-PasswordFiles in the finally
        block of the main flow.
    #>
    param([Parameter(Mandatory)][securestring]$Secret)

    $path = Join-Path ([System.IO.Path]::GetTempPath()) ("chatflow-deploy-{0}.tmp" -f [guid]::NewGuid())

    # Create it empty first so the ACL is in place before the secret is written.
    New-Item -ItemType File -Path $path -Force | Out-Null

    $acl = Get-Acl $path
    $acl.SetAccessRuleProtection($true, $false)   # drop inherited rules
    $acl.SetAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
        [System.Security.Principal.WindowsIdentity]::GetCurrent().Name,
        'FullControl', 'Allow')))
    Set-Acl -Path $path -AclObject $acl

    $plain = [System.Net.NetworkCredential]::new('', $Secret).Password
    try {
        # plink reads the first line; no trailing newline needed.
        [System.IO.File]::WriteAllText($path, $plain, [System.Text.UTF8Encoding]::new($false))
    }
    finally {
        $plain = $null
    }

    return $path
}

function Get-PasswordFile {
    <#
        One password per SSH target, prompted on first use. The two hosts may well
        have different credentials, so they are cached separately.
    #>
    param([Parameter(Mandatory)][string]$Target)

    if ($script:PasswordFiles.ContainsKey($Target)) {
        return $script:PasswordFiles[$Target]
    }

    $secret = $SshPassword
    if ($null -eq $secret) {
        Write-Host ''
        $secret = Read-Host -Prompt "SSH password for $Target" -AsSecureString
    }

    if ($null -eq $secret -or $secret.Length -eq 0) {
        throw "No password entered for $Target."
    }

    $file = New-PasswordFile -Secret $secret
    $script:PasswordFiles[$Target] = $file
    return $file
}

function Remove-PasswordFiles {
    foreach ($file in $script:PasswordFiles.Values) {
        if (-not (Test-Path $file)) { continue }
        try {
            # Overwrite before unlinking so the secret does not linger in slack space.
            [System.IO.File]::WriteAllText($file, ('0' * 256))
            Remove-Item $file -Force
        }
        catch {
            Write-Warn "Could not remove password file $file - delete it manually."
        }
    }
    $script:PasswordFiles.Clear()
}

function Get-OpenSshArgs {
    param([switch]$ForScp)

    $sshArgs = @()
    if (-not [string]::IsNullOrWhiteSpace($SshKey)) {
        $sshArgs += @('-i', $SshKey)
    }
    # Fail fast rather than hang on an unreachable host or a password prompt.
    $sshArgs += @('-o', 'ConnectTimeout=15', '-o', 'BatchMode=yes')
    if ($ForScp) { $sshArgs += '-C' }
    return $sshArgs
}

function Get-PuttyArgs {
    param([Parameter(Mandatory)][string]$Target, [switch]$ForScp)

    # -batch refuses interactive prompts, so an uncached host key aborts with a
    # message instead of hanging a scripted deploy forever.
    $puttyArgs = @('-batch', '-pwfile', (Get-PasswordFile -Target $Target))
    if ($ForScp) { $puttyArgs += '-C' }
    return $puttyArgs
}

function Confirm-HostKey {
    <#
        plink keeps its host key cache in the registry, independent of OpenSSH's
        known_hosts, so a host you already use with ssh is still unknown to it.
        With -AcceptHostKeys the first connection to each target runs WITHOUT
        -batch, so PuTTY prints the fingerprint and waits for y/n. The key is
        never accepted automatically - that check is the whole point of it.
    #>
    param([Parameter(Mandatory)][string]$Target)

    if (-not $AcceptHostKeys -or $DryRun) { return }
    if (-not (Test-UsePasswordAuth)) { return }
    if ($script:HostKeysConfirmed.ContainsKey($Target)) { return }

    $script:HostKeysConfirmed[$Target] = $true

    Write-Step "Checking host key for $Target"
    Write-Info 'If PuTTY shows a fingerprint, verify it matches your server, then answer y.'

    $plink = Get-PuttyTool -Name 'plink'
    $passwordFile = Get-PasswordFile -Target $Target

    # No -batch here: this is the one call allowed to prompt.
    & $plink '-ssh', '-pwfile', $passwordFile, $Target, 'true'
    if ($LASTEXITCODE -ne 0) {
        throw "Could not establish an SSH session to $Target (exit $LASTEXITCODE). Check the host, username and password."
    }

    Write-Ok "$Target reachable"
}

function Resolve-SshFailure {
    <#
        plink's host key cache lives in the registry, separate from OpenSSH's
        known_hosts, so a host you have already used with ssh still looks unknown
        the first time plink sees it.
    #>
    param([Parameter(Mandatory)][string]$Target, [Parameter(Mandatory)][string]$Message)

    if ($Message -match 'host key|hostkey|verification failed|cache') {
        throw @"
$Message

The host key for $Target is not cached yet. plink keeps its own cache in the
registry, separate from OpenSSH's known_hosts, so a host you already use with
ssh still looks unknown to it.

Re-run with -AcceptHostKeys and confirm the fingerprint when prompted:

    ./tools/publish-release.ps1 -Version <ver> -AcceptHostKeys
"@
    }

    throw $Message
}

function Invoke-Ssh {
    param(
        [Parameter(Mandatory)][string]$Target,
        [Parameter(Mandatory)][string]$Command,
        [string]$What
    )

    # This script is CRLF (Windows PowerShell needs that), but a remote bash sees
    # the carriage returns as part of each token: "set -e\r" is an invalid option
    # and "cd /path\r" is a missing directory. Normalize before sending.
    $Command = $Command -replace "`r`n", "`n" -replace "`r", "`n"

    $label = if ($What) { $What } else { "ssh $Target" }

    if ($DryRun) {
        Write-Warn "[dry-run] ssh $Target `"$Command`""
        return
    }

    try {
        if (Test-UsePasswordAuth) {
            Confirm-HostKey -Target $Target
            Invoke-Native -Exe (Get-PuttyTool -Name 'plink') `
                -Arguments ((Get-PuttyArgs -Target $Target) + @($Target, $Command)) -What $label
        }
        else {
            Invoke-Native -Exe 'ssh' -Arguments ((Get-OpenSshArgs) + @($Target, $Command)) -What $label
        }
    }
    catch {
        Resolve-SshFailure -Target $Target -Message $_.Exception.Message
    }
}

function Copy-ToRemote {
    param(
        [Parameter(Mandatory)][string]$LocalPath,
        [Parameter(Mandatory)][string]$Target,
        [Parameter(Mandatory)][string]$RemotePath
    )

    $label = "scp $(Split-Path -Leaf $LocalPath) -> ${Target}:$RemotePath"

    if ($DryRun) {
        Write-Warn "[dry-run] $label"
        return
    }

    try {
        if (Test-UsePasswordAuth) {
            Confirm-HostKey -Target $Target
            Invoke-Native -Exe (Get-PuttyTool -Name 'pscp') `
                -Arguments ((Get-PuttyArgs -Target $Target -ForScp) + @($LocalPath, "${Target}:$RemotePath")) `
                -What $label
        }
        else {
            Invoke-Native -Exe 'scp' `
                -Arguments ((Get-OpenSshArgs -ForScp) + @($LocalPath, "${Target}:$RemotePath")) -What $label
        }
    }
    catch {
        Resolve-SshFailure -Target $Target -Message $_.Exception.Message
    }
}

function Invoke-SshCapture {
    <#
        Runs a remote command and returns stdout, or $null when it fails. Used for
        checks where a failure is informational rather than fatal.
    #>
    param([Parameter(Mandatory)][string]$Target, [Parameter(Mandatory)][string]$Command)

    if (Test-UsePasswordAuth) {
        $output = & (Get-PuttyTool -Name 'plink') @((Get-PuttyArgs -Target $Target) + @($Target, $Command)) 2>$null
    }
    else {
        $output = & ssh @((Get-OpenSshArgs) + @($Target, $Command)) 2>$null
    }

    if ($LASTEXITCODE -ne 0) { return $null }
    return $output
}

# --- Versioning ----------------------------------------------------------------

function Get-PythonExe {
    foreach ($candidate in @('python3', 'python', 'py')) {
        $command = Get-Command $candidate -ErrorAction SilentlyContinue
        if ($command) { return $command.Source }
    }

    throw @"
python3 is required to patch appsettings.json without reformatting the whole
file. Install it from https://www.python.org/ (or the Microsoft Store) and
re-run.
"@
}

function ConvertTo-PatchJson {
    param([Parameter(Mandatory)][System.Collections.Specialized.OrderedDictionary]$Section)
    return ([PSCustomObject]$Section | ConvertTo-Json -Depth 16)
}

function New-PatchFile {
    <#
        Serializes the AppUpdate object to a temp file for patch-appupdate.py.
        Windows PowerShell's ConvertTo-Json formatting is ugly, but it never
        reaches a committed file - python re-renders it on the way in.
    #>
    param([Parameter(Mandatory)][System.Collections.Specialized.OrderedDictionary]$Section)

    $path = Join-Path ([System.IO.Path]::GetTempPath()) ("appupdate-{0}.json" -f [guid]::NewGuid())
    (ConvertTo-PatchJson -Section $Section) | Set-Content -Path $path -Encoding UTF8
    return $path
}

function Get-CurrentVersion {
    $content = Get-Content $BlazorProject -Raw
    if ($content -match '<ApplicationDisplayVersion>([^<]+)</ApplicationDisplayVersion>') {
        return $Matches[1].Trim()
    }
    throw "Could not read ApplicationDisplayVersion from $BlazorProject"
}

function Set-ProjectVersions {
    param([Parameter(Mandatory)][string]$NewVersion)

    $blazor = Get-Content $BlazorProject -Raw
    $blazor = $blazor -replace '<ApplicationDisplayVersion>[^<]*</ApplicationDisplayVersion>', "<ApplicationDisplayVersion>$NewVersion</ApplicationDisplayVersion>"

    # The Android versionCode must strictly increase or the APK will not install
    # over an existing one.
    if ($blazor -match '<ApplicationVersion>(\d+)</ApplicationVersion>') {
        $nextCode = [int]$Matches[1] + 1
        $blazor = $blazor -replace '<ApplicationVersion>\d+</ApplicationVersion>', "<ApplicationVersion>$nextCode</ApplicationVersion>"
        Write-Info "Android versionCode -> $nextCode"
    }

    if ($DryRun) {
        Write-Warn "[dry-run] ChatAppBlazor.csproj would become $NewVersion (not written)"
        return
    }

    Set-Content -Path $BlazorProject -Value $blazor -Encoding UTF8 -NoNewline
    Write-Ok "ChatAppBlazor.csproj -> $NewVersion"
}

function Get-ArtifactNames {
    param([Parameter(Mandatory)][string]$ReleaseVersion)

    return [ordered]@{
        windows = "ChatLast$ReleaseVersion.zip"
        android = "ChatLast$ReleaseVersion.apk"
    }
}

function New-AppUpdateSection {
    <#
        Builds the AppUpdate object the server hands to clients. Only platforms in
        this release get a fresh URL; anything else (notably the linux entry, which
        nothing currently builds) keeps whatever appsettings.json already has, so a
        partial release never advertises a file that was not uploaded.
    #>
    param(
        [Parameter(Mandatory)][string]$ReleaseVersion,
        [Parameter(Mandatory)][string]$Notes
    )

    $existing = (Get-Content $ServerSettings -Raw | ConvertFrom-Json).AppUpdate
    $names = Get-ArtifactNames -ReleaseVersion $ReleaseVersion
    $baseUrl = $DownloadBaseUrl.TrimEnd('/')

    $urls = [ordered]@{}
    $existingUrlNames = @()
    if ($existing.PSObject.Properties.Name -contains 'DownloadUrls' -and $null -ne $existing.DownloadUrls) {
        $existingUrlNames = $existing.DownloadUrls.PSObject.Properties.Name
    }

    # Union of what this release builds and what the server already advertises.
    foreach ($platform in (@($names.Keys) + $existingUrlNames | Select-Object -Unique)) {
        if ($Artifacts -contains $platform) {
            $urls[$platform] = "$baseUrl/$($names[$platform])"
        }
        elseif ($existingUrlNames -contains $platform) {
            $urls[$platform] = [string]$existing.DownloadUrls.$platform
            Write-Warn "$platform is not in this release; keeping $($urls[$platform])"
        }
    }

    $windowsUrl = if ($urls.Contains('windows')) { $urls['windows'] } else { $baseUrl }

    return [ordered]@{
        LatestVersion = $ReleaseVersion
        # Legacy flat key; still the last-resort fallback in UpdateController.
        DownloadUrl   = $windowsUrl
        DownloadUrls  = $urls
        ReleaseNotes  = $Notes
    }
}

function Set-LocalAppUpdate {
    param([Parameter(Mandatory)][System.Collections.Specialized.OrderedDictionary]$Section)

    if ($DryRun) {
        Write-Warn "[dry-run] appsettings.json AppUpdate would become:"
        (ConvertTo-PatchJson -Section $Section) -split "`n" | ForEach-Object { Write-Info $_.TrimEnd() }
        return
    }

    # Patch through the same python helper the server uses, so both copies keep
    # identical formatting and the diff stays limited to the AppUpdate block.
    $patchFile = New-PatchFile -Section $Section
    try {
        Invoke-Native -Exe (Get-PythonExe) -Arguments @($PatchScript, $ServerSettings, $patchFile) `
            -What 'patch local appsettings.json' | Out-Null
    }
    finally {
        Remove-Item $patchFile -Force -ErrorAction SilentlyContinue
    }

    Write-Ok "appsettings.json AppUpdate -> $($Section.LatestVersion)"
}

# --- Build ---------------------------------------------------------------------

function Build-WindowsClient {
    param([Parameter(Mandatory)][string]$TargetName)

    Write-Step "Publishing Windows client"
    $publishDir = Join-Path $RepoRoot 'artifacts/build/win-x64'

    # No -r here on purpose. ChatAppBlazor multi-targets, and a command-line RID
    # becomes a global property that the android TFM also picks up during restore,
    # where it fails looking for Microsoft.NETCore.App.Runtime.Mono.win-x64. The
    # MAUI Windows SDK already resolves win-x64 on its own, and Release config
    # already sets SelfContained=False.
    Invoke-Native -Exe 'dotnet' -Arguments @(
        'publish', $BlazorProject,
        '-f', $WindowsTfm,
        '-c', 'Release',
        '-o', $publishDir,
        '--nologo'
    ) -What "dotnet publish ChatAppBlazor ($WindowsTfm)"

    # The csproj's ZipWindowsPublishOutput target drops the zip one level above
    # the publish directory.
    $zip = Get-ChildItem -Path (Split-Path -Parent $publishDir) -Filter 'ChatAppBlazor-*.zip' -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1

    if (-not $zip) {
        throw "Windows publish zip not found. Is GenerateWindowsPublishZip still enabled in ChatAppBlazor.csproj?"
    }

    $destination = Join-Path $OutputDir $TargetName
    Copy-Item $zip.FullName $destination -Force
    Write-Ok "$TargetName ($([math]::Round($zip.Length / 1MB, 1)) MB)"
}

function Build-AndroidClient {
    param([Parameter(Mandatory)][string]$TargetName)

    Write-Step "Publishing Android client (Profiled AOT - the slow step)"

    Invoke-Native -Exe 'dotnet' -Arguments @(
        'publish', $BlazorProject,
        '-f', $AndroidTfm,
        '-c', 'Release',
        '--nologo'
    ) -What "dotnet publish ChatAppBlazor ($AndroidTfm)"

    $binDir = Join-Path $RepoRoot "ChatAppBlazor/bin/Release/$AndroidTfm"
    # Only the signed APK will install on a device.
    $apk = Get-ChildItem -Path $binDir -Filter '*-Signed.apk' -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1

    if (-not $apk) {
        throw "No signed APK found under $binDir"
    }

    $destination = Join-Path $OutputDir $TargetName
    Copy-Item $apk.FullName $destination -Force
    Write-Ok "$TargetName ($([math]::Round($apk.Length / 1MB, 1)) MB)"
}

function Build-Server {
    Write-Step "Publishing ChatServer ($ServerRid, ReadyToRun)"
    $publishDir = Join-Path $RepoRoot "artifacts/build/$ServerRid"

    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

    Invoke-Native -Exe 'dotnet' -Arguments @(
        'publish', $ServerProject,
        '-c', 'Release',
        '-r', $ServerRid,
        '--self-contained', 'false',
        '-o', $publishDir,
        '--nologo'
    ) -What "dotnet publish ChatServer ($ServerRid)"

    $tarball = Join-Path $OutputDir 'chatserver.tar.gz'
    if (Test-Path $tarball) { Remove-Item $tarball -Force }

    # appsettings.json is deliberately excluded in Merge mode: the remote copy
    # holds production secrets and is patched separately.
    $tarArgs = @('-czf', $tarball, '-C', $publishDir)
    if ($SettingsMode -eq 'Merge') {
        $tarArgs += '--exclude=./appsettings.json'
    }
    $tarArgs += '.'

    Invoke-Native -Exe 'tar' -Arguments $tarArgs -What 'tar chatserver.tar.gz'

    $size = (Get-Item $tarball).Length
    Write-Ok "chatserver.tar.gz ($([math]::Round($size / 1MB, 1)) MB)"

    # Emit exactly one string, whatever the callee wrote to the console.
    return [string]$tarball
}

# --- Deploy --------------------------------------------------------------------

function Publish-Artifacts {
    param([Parameter(Mandatory)][System.Collections.Specialized.OrderedDictionary]$Names)

    Write-Step "Uploading client artifacts to $DownloadHost"
    Invoke-Ssh -Target $DownloadHost -Command "mkdir -p '$DownloadPath'" -What "ensure $DownloadPath"

    foreach ($platform in $Artifacts) {
        $name = $Names[$platform]
        $local = Join-Path $OutputDir $name
        if (-not (Test-Path $local)) {
            if ($DryRun) {
                Write-Warn "$name is not built yet; showing the remaining steps anyway."
            }
            else {
                throw "Missing artifact $name - run without -SkipBuild first."
            }
        }

        # Upload under a temp name and rename into place, so a half-transferred
        # file is never served to a client that is mid-update.
        Copy-ToRemote -LocalPath $local -Target $DownloadHost -RemotePath "$DownloadPath/$name.uploading"
        Invoke-Ssh -Target $DownloadHost `
            -Command "mv -f '$DownloadPath/$name.uploading' '$DownloadPath/$name' && chmod 644 '$DownloadPath/$name'" `
            -What "publish $name"
        Write-Ok $name
    }
}

function Publish-Server {
    param([Parameter(Mandatory)][string]$Tarball)

    Write-Step "Deploying ChatServer to $ServerHost"

    Invoke-Ssh -Target $ServerHost -Command "mkdir -p '$ServerPath'" -What "ensure $ServerPath"
    Copy-ToRemote -LocalPath $Tarball -Target $ServerHost -RemotePath '/tmp/chatserver.tar.gz'

    # Stopping first is preferable but needs privileges the deploy account may not
    # have, so it is strictly best-effort. tar replaces files by unlink+create, so
    # a running process keeps its old inodes and extraction over it is safe.
    # --no-overwrite-dir: the archive carries a "./" entry, and without this tar
    #   tries to stamp its mtime onto the target directory. On panel-managed
    #   hosting the deploy user can write inside that directory but does not own
    #   it, so that single utime call fails the whole extraction.
    # --no-same-owner: never attempt chown; the deploy user is not root.
    $deployScript = @'
set -e
STOP_COMMAND
tar --no-overwrite-dir --no-same-owner -xzf /tmp/chatserver.tar.gz -C 'REMOTE_PATH'
rm -f /tmp/chatserver.tar.gz
chmod +x 'REMOTE_PATH/ChatServer' 2>/dev/null || true
echo extracted
'@ -replace 'STOP_COMMAND', "sudo -n systemctl stop '$ServiceName' 2>/dev/null || true" `
        -replace 'REMOTE_PATH', $ServerPath

    Invoke-Ssh -Target $ServerHost -Command $deployScript -What 'extract ChatServer'
}

function Update-RemoteAppSettings {
    param([Parameter(Mandatory)][System.Collections.Specialized.OrderedDictionary]$Section)

    if ($SettingsMode -eq 'Replace') {
        Write-Warn 'SettingsMode=Replace - the repo appsettings.json overwrites the remote one, including secrets.'
        Copy-ToRemote -LocalPath $ServerSettings -Target $ServerHost -RemotePath "$ServerPath/appsettings.json"
        return
    }

    Write-Step 'Patching remote appsettings.json (AppUpdate only)'

    $patchFile = New-PatchFile -Section $Section
    try {
        Copy-ToRemote -LocalPath $patchFile -Target $ServerHost -RemotePath "$ServerPath/appupdate.patch.json"
        Copy-ToRemote -LocalPath $PatchScript -Target $ServerHost -RemotePath "$ServerPath/patch-appupdate.py"
    }
    finally {
        Remove-Item $patchFile -Force -ErrorAction SilentlyContinue
    }

    # Merge with python3 so everything else in the file - Jwt key, connection
    # strings, Janus config that only exists on the server - is preserved.
    $mergeScript = @'
set -e
cd 'REMOTE_PATH'
if ! command -v python3 >/dev/null 2>&1; then
  echo "python3 not found on the server; rerun with -SettingsMode Replace" >&2
  exit 1
fi
python3 patch-appupdate.py appsettings.json appupdate.patch.json --backup
rm -f appupdate.patch.json patch-appupdate.py
'@ -replace 'REMOTE_PATH', $ServerPath

    Invoke-Ssh -Target $ServerHost -Command $mergeScript -What 'merge AppUpdate into appsettings.json'
}

function Restart-RemoteService {
    <#
        Returns $true when the new build is actually live. A failure here is not
        fatal: the files are already deployed, and the old process keeps serving
        the OLD appsettings it read at startup - so clients are still told about
        the previous version and nothing is announced that is not being served.
        That is a safe state to stop in, but the operator has to know about it.
    #>
    if (Test-RestartDisabled) {
        Write-Step 'Restart skipped (RestartCommand = none)'
        Write-Warn "Restart $ServiceName yourself to pick up the new build."
        return $false
    }

    $command = Get-RestartCommand
    Write-Step "Restarting: $command"

    $restartScript = @'
set -e
RESTART_COMMAND
sleep 3
if command -v systemctl >/dev/null 2>&1 && systemctl is-active --quiet 'SERVICE_NAME'; then
  echo "SERVICE_NAME is active"
fi
'@ -replace 'RESTART_COMMAND', $command -replace 'SERVICE_NAME', $ServiceName

    try {
        Invoke-Ssh -Target $ServerHost -Command $restartScript -What "restart $ServiceName"
        Write-Ok "$ServiceName restarted"
        return $true
    }
    catch {
        Write-Host ''
        Write-Warn 'RESTART FAILED - files are deployed but the old build is still running.'
        Write-Warn $_.Exception.Message
        Write-Host ''

        if ($_.Exception.Message -match 'terminal is required|password is required|sudo') {
            Write-Warn @"
The deploy account cannot use sudo without a terminal. On CloudPanel and most
shared hosting the site user has no sudo rights at all. Pick one:

  1. Restart from a shell that can:  sudo systemctl restart $ServiceName
  2. Grant NOPASSWD for just this unit, as root:
       echo "$($ServerHost.Split('@')[0]) ALL=(root) NOPASSWD: /usr/bin/systemctl restart $ServiceName, /usr/bin/systemctl stop $ServiceName" \
         > /etc/sudoers.d/$ServiceName-deploy
  3. Put a working command in publish-release.settings.json, e.g.
       "RestartCommand": "supervisorctl restart $ServiceName"
     or "none" to always restart by hand.
"@
        }

        return $false
    }
}

function Test-UpdateEndpoint {
    param([Parameter(Mandatory)][string]$ExpectedVersion)

    Write-Step "Verifying the update endpoint reports $ExpectedVersion"

    $check = "curl -fsS 'http://127.0.0.1:5001/api/update?platform=windows' || curl -fsSk 'https://127.0.0.1:5002/api/update?platform=windows'"

    if ($DryRun) {
        Write-Warn "[dry-run] ssh $ServerHost `"$check`""
        return
    }

    $response = Invoke-SshCapture -Target $ServerHost -Command $check
    if ([string]::IsNullOrWhiteSpace($response)) {
        Write-Warn 'Could not reach the update endpoint from the server; check it manually.'
        return
    }

    Write-Info $response
    if ($response -match [regex]::Escape($ExpectedVersion)) {
        Write-Ok "Update endpoint is serving $ExpectedVersion"
    }
    else {
        Write-Warn "Endpoint responded but did not mention $ExpectedVersion - check appsettings.json on the server."
    }
}

# --- Main ----------------------------------------------------------------------

Merge-Settings

if (-not $SkipDeploy) {
    Assert-DeploySettings
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = Get-CurrentVersion
    Write-Warn "No -Version given; reusing $Version from the csproj."
}

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version must look like 1.9.0 (got '$Version')."
}

if ([string]::IsNullOrWhiteSpace($ReleaseNotes)) {
    $ReleaseNotes = [string](Get-Content $ServerSettings -Raw | ConvertFrom-Json).AppUpdate.ReleaseNotes
    Write-Warn "No -ReleaseNotes given; keeping `"$ReleaseNotes`"."
}

# The download base URL is already encoded in the current appsettings.json, so
# recover it from there rather than writing a placeholder into the file.
if ([string]::IsNullOrWhiteSpace($DownloadBaseUrl)) {
    $currentUrl = [string](Get-Content $ServerSettings -Raw | ConvertFrom-Json).AppUpdate.DownloadUrl
    if ($currentUrl -match '^(https?://[^/]+(?:/.*)?)/[^/]+$') {
        $DownloadBaseUrl = $Matches[1]
        Write-Warn "No -DownloadBaseUrl; reusing $DownloadBaseUrl from appsettings.json."
    }
    else {
        throw "Could not derive a download base URL from AppUpdate.DownloadUrl ('$currentUrl'). Pass -DownloadBaseUrl."
    }
}

$names = Get-ArtifactNames -ReleaseVersion $Version

Write-Host ''
Write-Host "ChatFlow release $Version" -ForegroundColor White
Write-Host "  artifacts : $($Artifacts -join ', ')" -ForegroundColor Gray
Write-Host "  notes     : $ReleaseNotes" -ForegroundColor Gray
if (-not $SkipDeploy) {
    Write-Host "  downloads : ${DownloadHost}:$DownloadPath -> $DownloadBaseUrl" -ForegroundColor Gray
    Write-Host "  server    : ${ServerHost}:$ServerPath ($ServiceName)" -ForegroundColor Gray
    Write-Host "  auth      : $(Get-AuthDescription)" -ForegroundColor Gray
}
if ($DryRun) { Write-Host '  DRY RUN - no remote commands will execute' -ForegroundColor Yellow }

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

Write-Step "Stamping version $Version"
Set-ProjectVersions -NewVersion $Version
$appUpdate = New-AppUpdateSection -ReleaseVersion $Version -Notes $ReleaseNotes
Set-LocalAppUpdate -Section $appUpdate

$serverTarball = Join-Path $OutputDir 'chatserver.tar.gz'

if ($SkipBuild) {
    Write-Warn "-SkipBuild: reusing artifacts already in $OutputDir"
}
else {
    if ($Artifacts -contains 'windows') { Build-WindowsClient -TargetName $names['windows'] }
    if ($Artifacts -contains 'android') { Build-AndroidClient -TargetName $names['android'] }
    $serverTarball = Build-Server
}

if ($SkipDeploy) {
    Write-Step 'Build complete - deploy skipped'
    Get-ChildItem $OutputDir -File -Exclude 'appupdate.patch.json' | ForEach-Object {
        Write-Ok "$($_.Name)  ($([math]::Round($_.Length / 1MB, 1)) MB)"
    }
    return
}

$restarted = $false
try {
    # Artifacts first: the server must never announce a version whose files are
    # not downloadable yet.
    Publish-Artifacts -Names $names
    Publish-Server -Tarball $serverTarball
    Update-RemoteAppSettings -Section $appUpdate
    $restarted = Restart-RemoteService
    if ($restarted) {
        Test-UpdateEndpoint -ExpectedVersion $Version
    }
}
finally {
    # Runs even on a failed or interrupted deploy - the password must not be left
    # sitting in the temp directory.
    Remove-PasswordFiles
}

Write-Host ''
if ($restarted) {
    Write-Host "Release $Version deployed." -ForegroundColor Green
}
else {
    Write-Host "Release $Version uploaded, but NOT live - the server was not restarted." -ForegroundColor Yellow
    Write-Host 'Until it restarts, clients keep seeing the previous version.' -ForegroundColor Yellow
}

foreach ($platform in $Artifacts) {
    Write-Host "  $($DownloadBaseUrl.TrimEnd('/'))/$($names[$platform])" -ForegroundColor Gray
}
Write-Host ''
Write-Host 'Commit the version bump:' -ForegroundColor White
Write-Host '  git add ChatAppBlazor/ChatAppBlazor.csproj ChatServer/appsettings.json' -ForegroundColor Gray
Write-Host "  git commit -m `"Release $Version`"" -ForegroundColor Gray

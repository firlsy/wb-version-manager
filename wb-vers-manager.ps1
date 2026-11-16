<#
.SYNOPSIS
    wb-vers-manager - pin the WorkBuddy desktop client to a specific version.

.DESCRIPTION
    Reads the installed WorkBuddy version, reports any update that the background
    updater has already staged, and blocks that update from installing.

    Locking works by quarantining the staged installer and replacing it with a
    directory of the same name. A directory cannot be overwritten by a file write,
    so the updater's next download fails at the final rename step while the
    installed application keeps running untouched.

    Nothing inside the install directory is modified. No admin rights required.

.PARAMETER Command
    status  Show installed version, staged update and lock state. (default)
    lock    Quarantine the staged installer and install the placeholder.
    unlock  Remove the placeholder and restore the newest quarantined installer.
    list    List everything in the backup vault.

.PARAMETER AppRoot
    WorkBuddy install root. Defaults to the standard Program Files location.

.PARAMETER KeepInstaller
    With unlock: remove the lock but do not restore the quarantined installer.

.EXAMPLE
    .\wb-vers-manager.ps1 status

.EXAMPLE
    .\wb-vers-manager.ps1 lock

.EXAMPLE
    .\wb-vers-manager.ps1 unlock -KeepInstaller
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('status', 'lock', 'unlock', 'list')]
    [string]$Command = 'status',

    [string]$AppRoot,

    [switch]$KeepInstaller
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ----------------------------------------------------------------------------
# Paths
# ----------------------------------------------------------------------------

function Get-ProgramFilesRoot {
    if ($env:ProgramW6432) { return $env:ProgramW6432 }
    if ($env:ProgramFiles) { return $env:ProgramFiles }
    return 'C:\Program Files'
}

if (-not $AppRoot) {
    $roots = @()
    foreach ($pf in @($env:ProgramW6432, $env:ProgramFiles)) { if ($pf) { $roots += $pf } }
    foreach ($letter in 'C','D','E','F','G','H','I','J') { $roots += ('{0}:\Program Files' -f $letter) }
    $candidates = @()
    foreach ($root in ($roots | Select-Object -Unique)) {
        $candidates += ('{0}\WorkBuddyAI' -f $root)
        $candidates += ('{0}\WorkBuddy' -f $root)
    }
    $found = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if ($found) { $AppRoot = $found } else { $AppRoot = $candidates[0] }
}

$UpdaterDir    = Join-Path $env:LOCALAPPDATA '@genieworkbuddy-desktop-updater'
$InstallerName = 'installer.exe'
$StagedPath    = Join-Path $UpdaterDir $InstallerName
$StateDir      = Join-Path $env:LOCALAPPDATA 'wb-vers-manager'
$BackupDir     = Join-Path $StateDir 'backup'
$StateFile     = Join-Path $StateDir 'state.json'

# ----------------------------------------------------------------------------
# Helpers
# ----------------------------------------------------------------------------

function Write-Rule {
    param([string]$Text = '')
    Write-Host ('  ' + $Text)
}

function Format-Size {
    param([long]$Bytes)
    if ($Bytes -ge 1GB) { return ('{0:N1} GB' -f ($Bytes / 1GB)) }
    if ($Bytes -ge 1MB) { return ('{0:N1} MB' -f ($Bytes / 1MB)) }
    if ($Bytes -ge 1KB) { return ('{0:N1} KB' -f ($Bytes / 1KB)) }
    return ('{0} B' -f $Bytes)
}

function Get-InstalledVersion {
    # 产品版本在 install-manifest.json 的 appVersion 里；
    # 根目录的 version 文件是 Electron 版本（如 37.10.3-43），不能当产品版本用。
    $manifest = Join-Path $AppRoot 'resources\install-manifest.json'
    if (Test-Path -LiteralPath $manifest -PathType Leaf) {
        try {
            $json = Get-Content -LiteralPath $manifest -Raw -ErrorAction Stop | ConvertFrom-Json
            if ($json.appVersion) { return [string]$json.appVersion }
        } catch {}
    }
    $versionFile = Join-Path $AppRoot 'version'
    if (Test-Path -LiteralPath $versionFile -PathType Leaf) {
        $raw = Get-Content -LiteralPath $versionFile -Raw -ErrorAction SilentlyContinue
        if ($raw) { return $raw.Trim() }
    }
    return $null
}

function Get-StagedInfo {
    <# Returns $null, or a hashtable describing what sits at $StagedPath. #>
    if (-not (Test-Path -LiteralPath $StagedPath)) { return $null }

    $item = Get-Item -LiteralPath $StagedPath -Force
    if ($item.PSIsContainer) {
        return @{ Kind = 'directory'; Size = 0; Modified = $item.LastWriteTime }
    }
    return @{ Kind = 'file'; Size = $item.Length; Modified = $item.LastWriteTime }
}

function Test-Locked {
    $info = Get-StagedInfo
    if ($null -eq $info) { return $false }
    return ($info.Kind -eq 'directory')
}

# ----------------------------------------------------------------------------
# Startup guard (5.9+ clients download to %TEMP%\workbuddy-update-*)
# ----------------------------------------------------------------------------

function Get-RuntimeRoots {
    $roots = @()
    foreach ($name in @('.workbuddy', '.workbuddy-ai')) {
        $p = Join-Path $env:USERPROFILE $name
        if (Test-Path -LiteralPath $p -PathType Container) { $roots += $p }
    }
    return $roots
}

function Set-JsonFlag {
    param([string]$Path, [string]$Name, [bool]$Blocked)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
    try {
        $obj = (Get-Content -LiteralPath $Path -Raw -ErrorAction Stop) | ConvertFrom-Json -ErrorAction Stop
        if ($Blocked) {
            $obj | Add-Member -NotePropertyName $Name -NotePropertyValue $false -Force
        } else {
            $obj.PSObject.Properties.Remove($Name)
        }
        $json = $obj | ConvertTo-Json -Depth 100
        [IO.File]::WriteAllText($Path, $json, (New-Object System.Text.UTF8Encoding($false)))
        return $true
    } catch {
        return $false
    }
}

function Set-StartupBlock {
    param([bool]$Blocked)
    $n = 0
    foreach ($root in (Get-RuntimeRoots)) {
        if (Set-JsonFlag -Path (Join-Path $root 'app\app-config.json') -Name 'startupForceAutoUpdate' -Blocked $Blocked) { $n++ }
        if (Set-JsonFlag -Path (Join-Path $root 'settings.json') -Name 'startupForceAutoUpdate' -Blocked $Blocked) { $n++ }
    }
    return $n
}

function Clear-InstallFailure {
    $n = 0
    foreach ($root in (Get-RuntimeRoots)) {
        foreach ($f in @('win-install-failure.json', 'win-install-attempt.json')) {
            $p = Join-Path $root ('app\{0}' -f $f)
            if (Test-Path -LiteralPath $p -PathType Leaf) {
                Remove-Item -LiteralPath $p -Force
                $n++
            }
        }
    }
    return $n
}

function Get-UpdateCacheDirs {
    $tmp = [IO.Path]::GetTempPath().TrimEnd('\')
    return @(Get-ChildItem -LiteralPath $tmp -Directory -ErrorAction SilentlyContinue |
             Where-Object { $_.Name -like 'workbuddy-update-*' })
}

function Move-CachedInstallers {
    $store = Join-Path $PSScriptRoot 'store'
    $moved = 0
    foreach ($dir in (Get-UpdateCacheDirs)) {
        $files = Get-ChildItem -LiteralPath $dir.FullName -File -ErrorAction SilentlyContinue |
                 Where-Object { $_.Name -match '^WorkBuddy-Setup-(\d+(\.\d+)+)\.exe$' }
        foreach ($file in $files) {
            $null = $file.Name -match '^WorkBuddy-Setup-(\d+(\.\d+)+)\.exe$'
            $ver = $Matches[1]
            $appId = if ($file.FullName -like '*-workbuddy-ai\*') { 'workbuddyai' } else { 'workbuddy' }
            $targetDir = Join-Path $store $appId
            if (-not (Test-Path -LiteralPath $targetDir)) { New-Item -ItemType Directory -Path $targetDir -Force | Out-Null }
            $target = Join-Path $targetDir ('WorkBuddy-win32-x64-user-{0}-recycled.exe' -f $ver)
            try {
                Move-Item -LiteralPath $file.FullName -Destination $target -Force
                $moved++
            } catch {}
        }
    }
    return $moved
}

function Set-CacheWriteBlock {
    param([bool]$Blocked)
    $who = '{0}\{1}' -f $env:USERDOMAIN, $env:USERNAME
    foreach ($dir in (Get-UpdateCacheDirs)) {
        if ($Blocked) {
            & icacls $dir.FullName /deny "${who}:(W)" 2>&1 | Out-Null
        } else {
            & icacls $dir.FullName /remove:d $who 2>&1 | Out-Null
        }
    }
}

function Save-State {
    param([string]$LockState, [string]$Note)
    if (-not (Test-Path -LiteralPath $StateDir)) {
        New-Item -ItemType Directory -Path $StateDir -Force | Out-Null
    }
    $state = [ordered]@{
        lockedAt         = (Get-Date).ToString('o')
        lockState        = $LockState
        installedVersion = (Get-InstalledVersion)
        appRoot          = $AppRoot
        updaterDir       = $UpdaterDir
        note             = $Note
    }
    $state | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $StateFile -Encoding UTF8
}

# ----------------------------------------------------------------------------
# Commands
# ----------------------------------------------------------------------------

function Invoke-Status {
    $version = Get-InstalledVersion
    $staged  = Get-StagedInfo
    $locked  = Test-Locked

    Write-Host ''
    Write-Host '  wb-vers-manager  status' -ForegroundColor Cyan
    Write-Rule ('-' * 60)

    $rootState = if (Test-Path -LiteralPath $AppRoot) { '' } else { '  [not found]' }
    Write-Host ('  Install root      {0}{1}' -f $AppRoot, $rootState)

    if ($version) {
        Write-Host ('  Installed version {0}' -f $version)
    } else {
        Write-Host '  Installed version (unreadable)' -ForegroundColor Yellow
    }

    $updaterState = if (Test-Path -LiteralPath $UpdaterDir) { '' } else { '  [absent]' }
    Write-Host ('  Updater cache     {0}{1}' -f $UpdaterDir, $updaterState)

    if ($null -eq $staged) {
        Write-Host '  Staged installer  none'
    } elseif ($staged.Kind -eq 'file') {
        Write-Host ('  Staged installer  {0}  ({1:yyyy-MM-dd HH:mm})' -f (Format-Size $staged.Size), $staged.Modified)
    } else {
        Write-Host '  Staged installer  placeholder directory (lock active)'
    }

    if ($locked) {
        Write-Host '  Lock state        LOCKED' -ForegroundColor Green
    } else {
        Write-Host '  Lock state        UNLOCKED' -ForegroundColor Yellow
    }

    Write-Rule ('-' * 60)

    if ($locked) {
        Write-Host '  The updater cannot stage a new installer while locked.'
        Write-Host '  Run ".\wb-vers-manager.ps1 unlock" to release.'
    } elseif ($null -ne $staged -and $staged.Kind -eq 'file') {
        Write-Host '  An update is staged and will install on next launch.' -ForegroundColor Yellow
        Write-Host '  Run ".\wb-vers-manager.ps1 lock" to block it.'
    } elseif ($null -eq $staged) {
        Write-Host '  Nothing staged. The updater may still fetch a build later.'
        Write-Host '  Run ".\wb-vers-manager.ps1 lock" to block it in advance.'
    }
    Write-Host ''
}

function Invoke-Lock {
    Write-Host ''
    Write-Host '  wb-vers-manager  lock' -ForegroundColor Cyan
    Write-Rule ('-' * 60)

    if (Test-Locked) {
        Write-Host '  Already locked - placeholder directory is in place.' -ForegroundColor Green
        Write-Host ''
        return
    }

    if (-not (Test-Path -LiteralPath $UpdaterDir)) {
        New-Item -ItemType Directory -Path $UpdaterDir -Force | Out-Null
    }

    # Step 1 - quarantine any staged installer
    $staged = Get-StagedInfo
    if ($null -ne $staged -and $staged.Kind -eq 'file') {
        if (-not (Test-Path -LiteralPath $BackupDir)) {
            New-Item -ItemType Directory -Path $BackupDir -Force | Out-Null
        }
        $stamp  = Get-Date -Format 'yyyyMMdd_HHmmss'
        $target = Join-Path $BackupDir ('installer_{0}.exe' -f $stamp)
        try {
            Move-Item -LiteralPath $StagedPath -Destination $target -Force
            Write-Host ('  [1/5] Quarantined installer.exe -> backup\{0}  ({1})' -f (Split-Path $target -Leaf), (Format-Size $staged.Size))
        } catch {
            Write-Host '  [1/5] Could not move the staged installer.' -ForegroundColor Red
            Write-Host ('        {0}' -f $_.Exception.Message) -ForegroundColor Red
            Write-Host '        Close WorkBuddy and retry.' -ForegroundColor Yellow
            Write-Host ''
            throw
        }
    } else {
        Write-Host '  [1/5] Nothing staged in the legacy updater cache - skipping.'
    }

    # Step 2 - drop the placeholder directory
    New-Item -ItemType Directory -Path $StagedPath -Force | Out-Null
    Write-Host '  [2/5] Placeholder directory created at updater cache'

    # Step 3 - turn off startup force auto-update (app-config.json + settings.json)
    $flags = Set-StartupBlock -Blocked $true
    Write-Host ('  [3/5] startupForceAutoUpdate=false written to {0} config file(s)' -f $flags)

    # Step 4 - recycle installers the client already downloaded
    $moved = Move-CachedInstallers
    Write-Host ('  [4/5] Recycled {0} cached installer(s) into store\' -f $moved)

    # Step 5 - reset the install-failure circuit and deny writes to the cache dir
    $cleared = Clear-InstallFailure
    Set-CacheWriteBlock -Blocked $true
    Write-Host ('  [5/5] Cleared {0} failure record(s); cache directories are now write-protected' -f $cleared)

    Save-State -LockState 'locked' -Note 'startup flag off, cache recycled, writes denied'

    Write-Rule ('-' * 60)
    Write-Host '  Locked. No startup update, no download, no elevation prompt.' -ForegroundColor Green
    Write-Host ''
}

function Invoke-Unlock {
    Write-Host ''
    Write-Host '  wb-vers-manager  unlock' -ForegroundColor Cyan
    Write-Rule ('-' * 60)

    $staged = Get-StagedInfo
    if ($null -eq $staged -or $staged.Kind -ne 'directory') {
        Write-Host '  [1/5] No placeholder directory - nothing to release.'
    } else {
        $children = Get-ChildItem -LiteralPath $StagedPath -Force -ErrorAction SilentlyContinue
        if ($children) {
            Write-Host '  [1/5] Placeholder is not empty - refusing to delete it.' -ForegroundColor Red
            Write-Host ('        Inspect {0} manually.' -f $StagedPath) -ForegroundColor Yellow
            Write-Host ''
            return
        }
        Remove-Item -LiteralPath $StagedPath -Force
        Write-Host '  [1/5] Placeholder directory removed'
    }

    # Step 2 - optionally restore the newest quarantined installer
    $latest = $null
    if (Test-Path -LiteralPath $BackupDir) {
        $latest = Get-ChildItem -LiteralPath $BackupDir -Filter 'installer_*.exe' -File -ErrorAction SilentlyContinue |
                  Sort-Object LastWriteTime -Descending |
                  Select-Object -First 1
    }

    if ($KeepInstaller) {
        Write-Host '  [2/5] -KeepInstaller set - quarantined installer left in the vault.'
    } elseif ($null -ne $latest) {
        Copy-Item -LiteralPath $latest.FullName -Destination $StagedPath -Force
        Write-Host ('  [2/5] Restored {0} ({1})' -f $latest.Name, (Format-Size $latest.Length))
    } else {
        Write-Host '  [2/5] No quarantined installer to restore.'
    }

    # Step 3 - let the client update itself again
    $flags = Set-StartupBlock -Blocked $false
    Write-Host ('  [3/5] startupForceAutoUpdate restored in {0} config file(s)' -f $flags)

    # Step 4 - drop the write protection on the update cache
    Set-CacheWriteBlock -Blocked $false
    Write-Host '  [4/5] Cache directories are writable again'

    # Step 5 - recycled installers stay in store\ so they can still be used for rollback
    Write-Host '  [5/5] Recycled installers left in store\ for rollback'

    Save-State -LockState 'unlocked' -Note 'placeholder removed, startup flag restored'

    Write-Rule ('-' * 60)
    Write-Host '  Unlocked. The client can update itself again.' -ForegroundColor Green
    Write-Host ''
}

function Invoke-List {
    Write-Host ''
    Write-Host '  wb-vers-manager  list' -ForegroundColor Cyan
    Write-Rule ('-' * 60)

    if (-not (Test-Path -LiteralPath $BackupDir)) {
        Write-Host '  Backup vault is empty.'
        Write-Host ''
        return
    }

    $items = Get-ChildItem -LiteralPath $BackupDir -Filter 'installer_*.exe' -File -ErrorAction SilentlyContinue |
             Sort-Object LastWriteTime -Descending

    if (-not $items) {
        Write-Host '  Backup vault is empty.'
        Write-Host ''
        return
    }

    $total = 0
    foreach ($item in $items) {
        $total += $item.Length
        Write-Host ('  {0}  {1,10}  {2:yyyy-MM-dd HH:mm}' -f $item.Name, (Format-Size $item.Length), $item.LastWriteTime)
    }

    Write-Rule ('-' * 60)
    Write-Host ('  {0} file(s), {1} total' -f $items.Count, (Format-Size $total))
    Write-Host ('  Location: {0}' -f $BackupDir)
    Write-Host ''
}

# ----------------------------------------------------------------------------
# Dispatch
# ----------------------------------------------------------------------------

switch ($Command) {
    'status' { Invoke-Status }
    'lock'   { Invoke-Lock }
    'unlock' { Invoke-Unlock }
    'list'   { Invoke-List }
}

[CmdletBinding(SupportsShouldProcess = $true)]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$appName = 'Instant File Share'
$agentExe = 'InstantFileShare.Agent.exe'
$dashboardExe = 'Instant File Share.exe'
$runKeyPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$contextMenuKeyPath = 'HKCU:\Software\Classes\*\shell\InstantFileShare'
$appDataPath = Join-Path $env:LOCALAPPDATA 'InstantFileShare'
$desktopShortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) "$appName.lnk"
$programShortcut = Join-Path ([Environment]::GetFolderPath('Programs')) "$appName\Dashboard.lnk"

function Stop-AppProcess {
  param([Parameter(Mandatory = $true)][string]$Name)

  $processes = Get-Process -Name ([System.IO.Path]::GetFileNameWithoutExtension($Name)) -ErrorAction SilentlyContinue
  foreach ($process in $processes) {
    if ($PSCmdlet.ShouldProcess($process.ProcessName, 'Stop process')) {
      Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }
  }
}

function Get-UninstallEntry {
  $registryRoots = @(
    'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall',
    'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall',
    'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
  )

  foreach ($root in $registryRoots) {
    $entries = Get-ChildItem $root -ErrorAction SilentlyContinue
    foreach ($entry in $entries) {
      $properties = Get-ItemProperty $entry.PSPath -ErrorAction SilentlyContinue
      if ($null -eq $properties) {
        continue
      }

      $displayNameProperty = $properties.PSObject.Properties['DisplayName']
      if ($null -eq $displayNameProperty -or $displayNameProperty.Value -notlike "$appName*") {
        continue
      }

      return $properties
    }
  }

  return $null
}

function Get-UninstallerPath {
  param([Parameter(Mandatory = $false)]$UninstallEntry)

  if ($null -eq $UninstallEntry) {
    return $null
  }

  $uninstallStringProperty = $UninstallEntry.PSObject.Properties['UninstallString']
  if ($null -eq $uninstallStringProperty -or [string]::IsNullOrWhiteSpace($uninstallStringProperty.Value)) {
    return $null
  }

  $uninstallString = [string]$uninstallStringProperty.Value
  if ($uninstallString.StartsWith('"')) {
    $closingQuoteIndex = $uninstallString.IndexOf('"', 1)
    if ($closingQuoteIndex -gt 1) {
      return $uninstallString.Substring(1, $closingQuoteIndex - 1)
    }
  }

  return ($uninstallString -split '\s+', 2)[0]
}

function Get-InstallLocation {
  param([Parameter(Mandatory = $false)]$UninstallEntry)

  if ($null -eq $UninstallEntry) {
    return $null
  }

  $installLocationProperty = $UninstallEntry.PSObject.Properties['InstallLocation']
  if ($null -ne $installLocationProperty -and -not [string]::IsNullOrWhiteSpace($installLocationProperty.Value)) {
    return [string]$installLocationProperty.Value
  }

  $uninstallerPath = Get-UninstallerPath -UninstallEntry $UninstallEntry
  if ([string]::IsNullOrWhiteSpace($uninstallerPath)) {
    return $null
  }

  return Split-Path -Path $uninstallerPath -Parent
}

Stop-AppProcess -Name $agentExe
Stop-AppProcess -Name $dashboardExe

$uninstallEntry = Get-UninstallEntry
$uninstallerPath = Get-UninstallerPath -UninstallEntry $uninstallEntry
$installLocation = Get-InstallLocation -UninstallEntry $uninstallEntry

if (-not [string]::IsNullOrWhiteSpace($uninstallerPath) -and (Test-Path $uninstallerPath)) {
  if ($PSCmdlet.ShouldProcess($uninstallerPath, 'Run installer uninstaller')) {
    & $uninstallerPath /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
  }
}

if (-not [string]::IsNullOrWhiteSpace($installLocation) -and (Test-Path $installLocation)) {
  if ($PSCmdlet.ShouldProcess($installLocation, 'Remove install directory')) {
    Remove-Item $installLocation -Recurse -Force -ErrorAction SilentlyContinue
  }
}

if (Test-Path $runKeyPath) {
  if ($PSCmdlet.ShouldProcess($runKeyPath, 'Remove startup registration')) {
    Remove-ItemProperty -Path $runKeyPath -Name 'InstantFileShare' -ErrorAction SilentlyContinue
  }
}

if (Test-Path $contextMenuKeyPath) {
  if ($PSCmdlet.ShouldProcess($contextMenuKeyPath, 'Remove file context menu registration')) {
    Remove-Item $contextMenuKeyPath -Recurse -Force -ErrorAction SilentlyContinue
  }
}

if (Test-Path $appDataPath) {
  if ($PSCmdlet.ShouldProcess($appDataPath, 'Remove application data')) {
    Remove-Item $appDataPath -Recurse -Force -ErrorAction SilentlyContinue
  }
}

foreach ($shortcut in @($desktopShortcut, $programShortcut)) {
  if (Test-Path $shortcut) {
    if ($PSCmdlet.ShouldProcess($shortcut, 'Remove shortcut')) {
      Remove-Item $shortcut -Force -ErrorAction SilentlyContinue
    }
  }
}

Write-Host 'Instant File Share cleanup completed.'

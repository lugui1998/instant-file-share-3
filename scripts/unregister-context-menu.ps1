Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$verbKeyPath = 'HKCU:\Software\Classes\*\shell\InstantFileShare'

if (Test-Path $verbKeyPath) {
  Remove-Item -Path $verbKeyPath -Recurse -Force
  Write-Host "Removed 'Copy Share Link' from the current user's Explorer context menu."
} else {
  Write-Host "No Instant File Share file context-menu registration was found for the current user."
}

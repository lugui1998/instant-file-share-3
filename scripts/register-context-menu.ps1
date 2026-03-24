Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path "$PSScriptRoot\..").Path
$helperCandidates = @(
  (Join-Path $repoRoot 'build\shell-extension\Debug\instant_file_share_shell.exe'),
  (Join-Path $repoRoot 'build\shell-extension\Release\instant_file_share_shell.exe')
)

$helperPath = $helperCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $helperPath) {
  throw "instant_file_share_shell.exe was not found. Build the shell helper first with scripts/build-all.ps1 or the CMake shell build."
}

$verbKeyPath = 'HKCU:\Software\Classes\*\shell\InstantFileShare'
$commandKeyPath = Join-Path $verbKeyPath 'command'

New-Item -Path $verbKeyPath -Force | Out-Null
New-Item -Path $commandKeyPath -Force | Out-Null

Set-ItemProperty -Path $verbKeyPath -Name '(default)' -Value 'Copy Share Link'
Set-ItemProperty -Path $verbKeyPath -Name 'Icon' -Value $helperPath
Set-ItemProperty -Path $verbKeyPath -Name 'MUIVerb' -Value 'Copy Share Link'
Set-ItemProperty -Path $commandKeyPath -Name '(default)' -Value ('"{0}" "%1"' -f $helperPath)

Write-Host "Registered 'Copy Share Link' for files in the current user's Explorer context menu."
Write-Host "Helper path: $helperPath"
Write-Host "On Windows 11 this registry-based command usually appears in the classic menu under 'Show more options'."

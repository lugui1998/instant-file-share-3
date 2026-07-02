Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path "$PSScriptRoot\.."
$testOutputRoot = Join-Path $repoRoot 'artifacts\tmp-test-out'
$runningOnWindows = $env:OS -eq 'Windows_NT'
if (Get-Variable -Name IsWindows -ErrorAction SilentlyContinue) {
  $runningOnWindows = [bool]$IsWindows
}
$npmCommand = if ($runningOnWindows) { 'npm.cmd' } else { 'npm' }

function Invoke-ExternalCommand {
  param(
    [Parameter(Mandatory = $true)]
    [string]$Description,

    [Parameter(Mandatory = $true)]
    [string]$FilePath,

    [string[]]$Arguments = @()
  )

  & $FilePath @Arguments
  if ($LASTEXITCODE -ne 0) {
    throw "$Description failed with exit code $LASTEXITCODE."
  }
}

Write-Host 'Running solution tests...'
Write-Host "Using isolated test output: $testOutputRoot"

Invoke-ExternalCommand `
  -Description '.NET solution tests' `
  -FilePath 'dotnet' `
  -Arguments @(
    'test',
    (Join-Path $repoRoot 'InstantFileShare.slnx'),
    "-p:BaseOutputPath=$testOutputRoot\"
  )

Write-Host 'Running UI tests...'
Push-Location (Join-Path $repoRoot 'src\ui')
try {
  Invoke-ExternalCommand -Description 'UI tests' -FilePath $npmCommand -Arguments @('test')
}
finally {
  Pop-Location
}

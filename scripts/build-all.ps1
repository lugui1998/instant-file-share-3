Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path "$PSScriptRoot\..").Path
$uiPath = Join-Path $repoRoot 'src\ui'
$shellSourcePath = Join-Path $repoRoot 'src\shell-extension'
$shellBuildPath = Join-Path $repoRoot 'build\shell-extension'
$dotnetBuildOutputRoot = Join-Path $repoRoot 'artifacts\tmp-build-out'
$dotnetTestOutputRoot = Join-Path $repoRoot 'artifacts\tmp-test-out'

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

foreach ($outputRoot in @($dotnetBuildOutputRoot, $dotnetTestOutputRoot)) {
  Remove-Item $outputRoot -Recurse -Force -ErrorAction SilentlyContinue
  New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
}

Write-Host 'Building .NET solution...'
Write-Host "Using isolated build output: $dotnetBuildOutputRoot"
Invoke-ExternalCommand `
  -Description '.NET solution build' `
  -FilePath 'dotnet' `
  -Arguments @(
    'build',
    (Join-Path $repoRoot 'InstantFileShare.slnx'),
    "-p:BaseOutputPath=$dotnetBuildOutputRoot\"
  )

Write-Host 'Running solution tests...'
Write-Host "Using isolated test output: $dotnetTestOutputRoot"
Invoke-ExternalCommand `
  -Description '.NET solution tests' `
  -FilePath 'dotnet' `
  -Arguments @(
    'test',
    (Join-Path $repoRoot 'InstantFileShare.slnx'),
    "-p:BaseOutputPath=$dotnetTestOutputRoot\"
  )

Write-Host 'Installing UI dependencies...'
Push-Location $uiPath
try {
  Invoke-ExternalCommand -Description 'UI dependency install' -FilePath 'npm' -Arguments @('install')

  Write-Host 'Building UI...'
  Invoke-ExternalCommand -Description 'UI build' -FilePath 'npm' -Arguments @('run', 'build')
} finally {
  Pop-Location
}

Write-Host 'Configuring shell-extension build...'
Invoke-ExternalCommand -Description 'shell-extension configure' -FilePath 'cmake' -Arguments @('-S', $shellSourcePath, '-B', $shellBuildPath)

Write-Host 'Building shell-extension...'
Invoke-ExternalCommand -Description 'shell-extension build' -FilePath 'cmake' -Arguments @('--build', $shellBuildPath, '--config', 'Debug')

Write-Host 'Build completed successfully.'

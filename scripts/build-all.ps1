Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path "$PSScriptRoot\.."
$uiPath = Join-Path $repoRoot 'src\ui'
$shellSourcePath = Join-Path $repoRoot 'src\shell-extension'
$shellBuildPath = Join-Path $repoRoot 'build\shell-extension'

Write-Host 'Building .NET solution...'
dotnet build (Join-Path $repoRoot 'InstantFileShare.slnx')

Write-Host 'Running solution tests...'
dotnet test (Join-Path $repoRoot 'InstantFileShare.slnx')

Write-Host 'Installing UI dependencies...'
Push-Location $uiPath
try {
  npm install

  Write-Host 'Building UI...'
  npm run build
} finally {
  Pop-Location
}

Write-Host 'Configuring shell-extension build...'
cmake -S $shellSourcePath -B $shellBuildPath

Write-Host 'Building shell-extension...'
cmake --build $shellBuildPath --config Debug

Write-Host 'Build completed successfully.'

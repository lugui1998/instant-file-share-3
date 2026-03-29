Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path "$PSScriptRoot\.."
$testOutputRoot = Join-Path $repoRoot 'artifacts\tmp-test-out'

Write-Host 'Running solution tests...'
Write-Host "Using isolated test output: $testOutputRoot"

dotnet test `
  (Join-Path $repoRoot 'InstantFileShare.slnx') `
  -p:BaseOutputPath="$testOutputRoot\"

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path "$PSScriptRoot\..").Path
$projectPath = Join-Path $repoRoot 'src\agent\InstantFileShare.Agent\InstantFileShare.Agent.csproj'
$agentOutputPath = $null
$resolvedAgentOutputPath = Resolve-Path (Join-Path $repoRoot 'src\agent\InstantFileShare.Agent\bin') -ErrorAction SilentlyContinue
if ($null -ne $resolvedAgentOutputPath) {
  $agentOutputPath = $resolvedAgentOutputPath.Path
}

$agentProcesses = Get-Process -Name 'InstantFileShare.Agent' -ErrorAction SilentlyContinue
foreach ($process in $agentProcesses) {
  try {
    Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
  } catch {
  }
}

$dotnetProcesses = Get-Process -Name 'dotnet' -ErrorAction SilentlyContinue
foreach ($process in $dotnetProcesses) {
  try {
    $processPath = $process.Path
    if ([string]::IsNullOrWhiteSpace($processPath)) {
      continue
    }

    $commandLine = (Get-CimInstance Win32_Process -Filter "ProcessId = $($process.Id)" -ErrorAction SilentlyContinue).CommandLine
    if ([string]::IsNullOrWhiteSpace($commandLine)) {
      continue
    }

    if ($commandLine -like "*InstantFileShare.Agent.csproj*" -or
        ($agentOutputPath -and $commandLine -like "*$agentOutputPath*")) {
      Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }
  } catch {
  }
}

dotnet run --project $projectPath

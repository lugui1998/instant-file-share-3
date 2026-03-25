Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path "$PSScriptRoot\.."
$artifactsRoot = Join-Path $repoRoot 'artifacts'
$packageRoot = Join-Path $artifactsRoot 'package'
$stageRoot = Join-Path $packageRoot 'stage'
$agentStage = Join-Path $stageRoot 'agent'
$shellStage = Join-Path $stageRoot 'shell'
$uiStage = Join-Path $stageRoot 'ui'
$uiPath = Join-Path $repoRoot 'src\ui'
$shellSourcePath = Join-Path $repoRoot 'src\shell-extension'
$shellBuildPath = Join-Path $repoRoot 'build\shell-extension'
$installerScript = Join-Path $repoRoot 'installer\InstantFileShare.iss'
$installerOutput = Join-Path $packageRoot 'installer'

function Resolve-InnoSetupCompiler {
  $command = Get-Command 'iscc' -ErrorAction SilentlyContinue
  if ($null -ne $command) {
    return $command.Source
  }

  $registryPaths = @(
    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
  )

  foreach ($registryPath in $registryPaths) {
    $entries = Get-ChildItem $registryPath -ErrorAction SilentlyContinue
    foreach ($entry in $entries) {
      $properties = Get-ItemProperty $entry.PSPath -ErrorAction SilentlyContinue
      if ($null -eq $properties) {
        continue
      }

      $displayNameProperty = $properties.PSObject.Properties['DisplayName']
      if ($null -eq $displayNameProperty -or $displayNameProperty.Value -notlike 'Inno Setup*') {
        continue
      }

      $candidates = @(
        $(if ($properties.PSObject.Properties['InstallLocation']) { Join-Path $properties.InstallLocation 'ISCC.exe' }),
        $(if ($properties.PSObject.Properties['DisplayIcon']) { $properties.DisplayIcon })
      ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

      foreach ($candidate in $candidates) {
        $normalized = $candidate.Trim('"')
        if (Test-Path $normalized) {
          return $normalized
        }
      }
    }
  }

  $defaultCandidates = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
    'C:\Program Files\Inno Setup 6\ISCC.exe'
  )

  return $defaultCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}

Write-Host 'Cleaning staged package output...'
Remove-Item $stageRoot -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $installerOutput -Recurse -Force -ErrorAction SilentlyContinue

foreach ($directory in @($stageRoot, $agentStage, $shellStage, $uiStage, $installerOutput)) {
  New-Item -ItemType Directory -Path $directory -Force | Out-Null
}

Write-Host 'Publishing agent...'
dotnet publish `
  (Join-Path $repoRoot 'src\agent\InstantFileShare.Agent\InstantFileShare.Agent.csproj') `
  -c Release `
  -r win-x64 `
  --self-contained true `
  /p:PublishSingleFile=true `
  /p:IncludeNativeLibrariesForSelfExtract=true `
  -o $agentStage

Remove-Item (Join-Path $agentStage '*.pdb') -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $agentStage 'appsettings.Development.json') -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $agentStage 'web.config') -Force -ErrorAction SilentlyContinue

Write-Host 'Configuring shell helper...'
cmake -S $shellSourcePath -B $shellBuildPath

Write-Host 'Building shell helper (Release)...'
cmake --build $shellBuildPath --config Release

$shellExe = Join-Path $shellBuildPath 'Release\instant_file_share_shell.exe'
if (-not (Test-Path $shellExe)) {
  throw "Shell helper not found at $shellExe"
}

Copy-Item $shellExe -Destination $shellStage

Write-Host 'Packaging Electron dashboard...'
Push-Location $uiPath
try {
  npm install
  npm run package:win
} finally {
  Pop-Location
}

$uiPackageRoot = Join-Path $packageRoot 'ui\win-unpacked'
if (-not (Test-Path $uiPackageRoot)) {
  throw "Packaged UI not found at $uiPackageRoot"
}

Copy-Item (Join-Path $uiPackageRoot '*') -Destination $uiStage -Recurse -Force

$iscc = Resolve-InnoSetupCompiler
if ([string]::IsNullOrWhiteSpace($iscc)) {
  Write-Warning 'Inno Setup compiler (iscc) was not found. The staged files are ready in artifacts\package\stage.'
  exit 0
}

Write-Host 'Building installer with Inno Setup...'
& $iscc "/DStageDir=$stageRoot" "/DOutputDir=$installerOutput" $installerScript

Write-Host 'Installer build completed successfully.'

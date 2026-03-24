Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Write-Host 'Restoring .NET projects...'
dotnet restore "$PSScriptRoot\..\InstantFileShare.slnx"

Write-Host 'Installing UI dependencies...'
Push-Location "$PSScriptRoot\..\src\ui"
try {
  npm install
} finally {
  Pop-Location
}

Write-Host 'Configuring shell-extension build...'
cmake -S "$PSScriptRoot\..\src\shell-extension" -B "$PSScriptRoot\..\build\shell-extension"

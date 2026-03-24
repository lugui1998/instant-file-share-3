Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Push-Location "$PSScriptRoot\..\src\ui"
npm run electron:dev
Pop-Location

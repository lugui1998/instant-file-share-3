Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

dotnet run --project "$PSScriptRoot\..\src\agent\InstantFileShare.Agent\InstantFileShare.Agent.csproj"

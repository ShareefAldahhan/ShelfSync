param([switch]$Build)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if ($Build -or !(Test-Path (Join-Path $root 'artifacts/publish/ShelfSync.Api.dll'))) {
    & (Join-Path $PSScriptRoot 'Publish.ps1')
}
$priorEnvironment = $env:ASPNETCORE_ENVIRONMENT
Push-Location (Join-Path $root 'artifacts/publish')
try {
    # Development permits HTTP cookies on this loopback-only local release.
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    Write-Output 'ShelfSync: http://localhost:5000. Press Ctrl+C to stop.'
    dotnet ShelfSync.Api.dll --urls http://localhost:5000
} finally { $env:ASPNETCORE_ENVIRONMENT = $priorEnvironment; Pop-Location }

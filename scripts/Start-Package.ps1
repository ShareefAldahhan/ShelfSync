param([ValidateRange(1024,65535)][int]$Port=5000)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
& (Join-Path $PSScriptRoot 'Test-Package.ps1') -PackageRoot $root
$priorEnvironment=$env:ASPNETCORE_ENVIRONMENT
try {
    $env:ASPNETCORE_ENVIRONMENT='Development'
    Write-Output "ShelfSync local release: http://localhost:$Port. Press Ctrl+C to stop."
    & dotnet (Join-Path $root 'ShelfSync.Api.dll') --contentRoot $root --urls "http://localhost:$Port"
    if($LASTEXITCODE){throw 'ShelfSync stopped with an error. Check the startup message and docs/setup.md.'}
} finally { $env:ASPNETCORE_ENVIRONMENT=$priorEnvironment }

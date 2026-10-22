param([switch]$SkipInstall, [string]$Output = 'artifacts/publish')
$ErrorActionPreference = 'Stop'
Push-Location (Join-Path $PSScriptRoot '..')
try {
    if (!$SkipInstall) {
        npm --prefix frontend ci
        if ($LASTEXITCODE) { throw 'Dependency installation failed.' }
    }
    npm --prefix frontend run build
    if ($LASTEXITCODE) { throw 'Frontend build failed.' }
    dotnet publish backend/ShelfSync.Api/ShelfSync.Api.csproj -c Release -o $Output
    if ($LASTEXITCODE) { throw 'Backend publish failed.' }
    New-Item -ItemType Directory -Force "$Output/wwwroot" | Out-Null
    Copy-Item frontend/dist/* "$Output/wwwroot" -Recurse -Force
    Write-Output "Ready: $Output. Configure connection and operator credentials before running."
} finally { Pop-Location }

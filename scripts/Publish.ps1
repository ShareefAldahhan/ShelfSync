param([switch]$SkipInstall, [switch]$Archive, [string]$Output = 'artifacts/publish')
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
    $revision = git rev-parse --short HEAD
    $dirty = [bool](git status --porcelain)
    @{ revision=$revision; sourceHasLocalChanges=$dirty; builtAtUtc=[DateTime]::UtcNow.ToString('o'); framework='net10.0' } | ConvertTo-Json | Set-Content "$Output/release.json"
    if ($Archive) {
        Compress-Archive -Path "$Output/*" -DestinationPath "$Output.zip" -Force
        $checksum = (Get-FileHash "$Output.zip" -Algorithm SHA256).Hash.ToLowerInvariant()
        "$checksum  $([IO.Path]::GetFileName($Output)).zip" | Set-Content "$Output.zip.sha256"
        Write-Output "Package: $Output.zip (SHA-256: $checksum)"
    }
    Write-Output "Ready: $Output. Configure connection and operator credentials before running."
} finally { Pop-Location }

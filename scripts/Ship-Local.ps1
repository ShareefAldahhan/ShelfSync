#Requires -Version 7.0
param([string]$PostgresBin='C:\Program Files\PostgreSQL\18\bin',[int]$TestPort=55439)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    [xml]$project=Get-Content backend/ShelfSync.Api/ShelfSync.Api.csproj
    $version=[string]$project.Project.PropertyGroup.Version
    if($version -notmatch '^\d+\.\d+\.\d+$'){throw 'Release version must be major.minor.patch.'}
    $name="ShelfSync-$version-local-"+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')
    $package=Join-Path $root "artifacts/releases/$name"
    if(Test-Path -LiteralPath $package){throw 'Release directory already exists; choose a new build time.'}
    & (Join-Path $PSScriptRoot 'Test-ReleaseTools.ps1')
    # A fresh directory avoids packaging stale bundles, configuration or old files.
    & (Join-Path $PSScriptRoot 'Verify-Release.ps1') -PostgresBin $PostgresBin -Port $TestPort -PublishDirectory $package
    $auditDirectory=Join-Path $root "artifacts/releases/$name-evidence"
    New-Item -ItemType Directory -Path $auditDirectory -Force | Out-Null
    npm --prefix frontend audit --omit=dev --json | Set-Content (Join-Path $auditDirectory 'npm-audit.json')
    if($LASTEXITCODE){throw 'Production frontend dependency audit did not pass; inspect release evidence.'}
    dotnet list backend/ShelfSync.Api/ShelfSync.Api.csproj package --vulnerable --include-transitive --format json | Set-Content (Join-Path $auditDirectory 'dotnet-audit.json')
    if($LASTEXITCODE){throw 'Backend dependency audit could not complete.'}
    $audit=Get-Content (Join-Path $auditDirectory 'dotnet-audit.json') -Raw|ConvertFrom-Json
    foreach($projectAudit in $audit.projects){foreach($framework in $projectAudit.frameworks){
        foreach($dependency in @($framework.topLevelPackages)+@($framework.transitivePackages)){
            if($dependency.vulnerabilities){throw 'Backend dependency advisories require review before release.'}
        }
    }}
    Copy-Item database -Destination $package -Recurse
    Copy-Item docs -Destination $package -Recurse
    New-Item -ItemType Directory -Path (Join-Path $package 'scripts') | Out-Null
    foreach($script in @('Start-Package.ps1','Test-Package.ps1','Set-Operator.ps1','Set-Payments.ps1','Apply-Migrations.ps1')){
        Copy-Item (Join-Path $PSScriptRoot $script) -Destination (Join-Path $package 'scripts')
    }
    $release=Get-Content (Join-Path $package 'release.json') -Raw|ConvertFrom-Json -AsHashtable
    $release.version=$version
    $release.scope='Local deployment; Stripe test mode; local email preview'
    $release.requiredMigrations=@('001','002','003','004','005')
    $release.releaseGate='Passed: backend/frontend tests, published HTTP/access/transport checks, migration replay, build/lint and dependency audits'
    $release|ConvertTo-Json -Depth 5|Set-Content (Join-Path $package 'release.json')
    $files=@(Get-ChildItem -LiteralPath $package -Recurse -File | Sort-Object FullName | ForEach-Object {
        if($_.Name -match '^(secrets\.json|appsettings\..*\.json|\.env.*)$' -or $_.Extension -in @('.pfx','.p12','.pem','.dump','.bak')){throw "Private or unexpected file in release: $($_.Name)"}
        @{path=[IO.Path]::GetRelativePath($package,$_.FullName).Replace('\','/');sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
    })
    @{algorithm='SHA256';files=$files}|ConvertTo-Json -Depth 5|Set-Content (Join-Path $package 'manifest.json')
    & (Join-Path $PSScriptRoot 'Test-Package.ps1') -PackageRoot $package
    Compress-Archive -Path "$package/*" -DestinationPath "$package.zip"
    $hash=(Get-FileHash "$package.zip" -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $name.zip"|Set-Content "$package.zip.sha256"
    Write-Output "VERIFIED LOCAL RELEASE: $package.zip"
    Write-Output 'Credentials, database backups, and data-protection keys are not included. No public deployment or real payments were enabled.'
} finally { Pop-Location }

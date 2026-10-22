param([string]$PostgresBin = 'C:\Program Files\PostgreSQL\18\bin', [int]$Port = 55439, [switch]$SkipPublish)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
$running = $false
$cluster = Join-Path $root ('artifacts/verification-' + [guid]::NewGuid().ToString('N'))
$priorTest = $env:SHELFSYNC_TEST_CONNECTION
$priorConnection = $env:ConnectionStrings__ShelfSync
$priorPassword = $env:PGPASSWORD
try {
    if (!$SkipPublish) { & (Join-Path $PSScriptRoot 'Publish.ps1') }
    foreach ($tool in @('initdb','pg_ctl','psql')) {
        if (!(Test-Path (Join-Path $PostgresBin "$tool.exe"))) { throw "PostgreSQL tool missing: $tool" }
    }
    New-Item -ItemType Directory -Force $cluster | Out-Null
    $password = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
    $passwordFile = Join-Path $cluster 'bootstrap-password.txt'
    [IO.File]::WriteAllText($passwordFile, $password)
    try {
        & "$PostgresBin/initdb.exe" -D "$cluster/data" -U shelfsync_test --auth-host=scram-sha-256 --auth-local=scram-sha-256 --pwfile=$passwordFile --no-locale -E UTF8 | Out-Null
        if ($LASTEXITCODE) { throw 'Isolated PostgreSQL initialization failed.' }
    } finally { Remove-Item -LiteralPath $passwordFile -Force }
    $controller=Start-Process "$PostgresBin/pg_ctl.exe" -ArgumentList @('-D', ('"'+"$cluster/data"+'"'), '-l', ('"'+"$cluster/server.log"+'"'), '-o', ('"' + "-h 127.0.0.1 -p $Port" + '"'), '-w','-t','30','start') -WindowStyle Hidden -PassThru -RedirectStandardOutput "$cluster/controller.log" -RedirectStandardError "$cluster/controller-error.log"
    if (!$controller.WaitForExit(45000)) { throw 'Database controller timed out.' }
    if ($controller.ExitCode) { throw 'Isolated PostgreSQL did not start; check the chosen port.' }
    $running = $true
    $env:PGPASSWORD = $password
    & "$PostgresBin/psql.exe" -w -h 127.0.0.1 -p $Port -U shelfsync_test -d postgres -v ON_ERROR_STOP=1 -c 'CREATE DATABASE shelfsync_test;' | Out-Null
    if ($LASTEXITCODE) { throw 'Test database creation failed.' }
    & "$PostgresBin/psql.exe" -w -h 127.0.0.1 -p $Port -U shelfsync_test -d shelfsync_test -v ON_ERROR_STOP=1 -f database/schema.sql | Out-Null
    if ($LASTEXITCODE) { throw 'Test schema installation failed.' }
    $env:SHELFSYNC_TEST_CONNECTION = "Host=127.0.0.1;Port=$Port;Database=shelfsync_test;Username=shelfsync_test;Password=$password"
    dotnet test tests/ShelfSync.Tests -c Release --logger 'console;verbosity=minimal'
    if ($LASTEXITCODE) { throw 'Database tests failed.' }
    $env:ConnectionStrings__ShelfSync = $env:SHELFSYNC_TEST_CONNECTION
    $env:SHELFSYNC_SMOKE_PRODUCT = [guid]::NewGuid().ToString()
    $id = $env:SHELFSYNC_SMOKE_PRODUCT
    & "$PostgresBin/psql.exe" -w -h 127.0.0.1 -p $Port -U shelfsync_test -d shelfsync_test -v ON_ERROR_STOP=1 -c "INSERT INTO products(id,sku,name,price_cents) VALUES ('$id','RELEASE-TEST','Release verification product',100); INSERT INTO inventory(product_id,shelf_units,picked_units,safety_buffer,quarantined) VALUES ('$id',10,0,0,false);" | Out-Null
    if ($LASTEXITCODE) { throw 'HTTP fixture setup failed.' }
    & (Join-Path $PSScriptRoot 'Verify-Http.ps1') -Published
    if ($LASTEXITCODE) { throw 'Published workflow verification failed.' }
    $env:SHELFSYNC_SMOKE_PRODUCT = $null
    & (Join-Path $PSScriptRoot 'Verify-Http.ps1') -Published -ProductionHttpChecks
    if ($LASTEXITCODE) { throw 'Production transport verification failed.' }
    npm --prefix frontend run lint
    if ($LASTEXITCODE) { throw 'Frontend lint failed.' }
    Write-Output 'PASS: complete isolated database suite, published workflows, security checks, production HTTP rejection, and lint.'
} finally {
    if ($running) { & "$PostgresBin/pg_ctl.exe" -D "$cluster/data" -m fast -w -t 30 stop | Out-Null }
    $env:SHELFSYNC_TEST_CONNECTION=$priorTest
    $env:ConnectionStrings__ShelfSync=$priorConnection
    $env:PGPASSWORD=$priorPassword
    $env:SHELFSYNC_SMOKE_PRODUCT=$null
    Pop-Location
}

param([string]$AdminUser='postgres', [string]$PostgresBin='C:\Program Files\PostgreSQL\18\bin', [switch]$EnableProfileEditing)
$ErrorActionPreference='Stop'
$settings=Get-Content (Join-Path $env:APPDATA 'Microsoft/UserSecrets/f1743222-090c-4111-8a6c-12e064007dba/secrets.json') -Raw|ConvertFrom-Json
$connection=if($settings.'ConnectionStrings:ShelfSync'){$settings.'ConnectionStrings:ShelfSync'}else{$settings.ConnectionStrings.ShelfSync}
$parts=[System.Data.Common.DbConnectionStringBuilder]::new();$parts.set_ConnectionString($connection)
$database=[string]$parts['Database'];$appRole=[string]$parts['Username']
if($database -notin @('shelfsync_dev','shelfsync_test')){throw 'Only the named local ShelfSync demo databases are supported.'}
if($appRole -notmatch '^[a-z_][a-z0-9_]*$'){throw 'Application role requires manual administrative grant setup.'}
Write-Host "Apply additive product-profile migration to $database; stock and orders are preserved."
$secure=Read-Host "PostgreSQL password for $AdminUser (not your website password)" -AsSecureString
$priorPassword=$env:PGPASSWORD
try {
 $env:PGPASSWORD=[Net.NetworkCredential]::new('',$secure).Password
 foreach($migration in Get-ChildItem (Join-Path $PSScriptRoot '../database/migrations/*.sql')|Sort-Object Name){
  & "$PostgresBin/psql.exe" -w -h 127.0.0.1 -U $AdminUser -d $database -v ON_ERROR_STOP=1 -f $migration.FullName
  if($LASTEXITCODE){throw 'Migration failed.'}
 }
 & "$PostgresBin/psql.exe" -w -h 127.0.0.1 -U $AdminUser -d $database -v ON_ERROR_STOP=1 -c "GRANT SELECT ON product_profiles TO $appRole;"
 if($LASTEXITCODE){throw 'Application read permission could not be granted.'}
 if ($EnableProfileEditing) {
  & "$PostgresBin/psql.exe" -w -h 127.0.0.1 -U $AdminUser -d $database -v ON_ERROR_STOP=1 -c "GRANT INSERT, UPDATE ON product_profiles TO $appRole;"
  if ($LASTEXITCODE) { throw 'Profile edit permission grant failed.' }
 }
 Write-Host 'Product metadata migration completed.' -ForegroundColor Green
} finally {$env:PGPASSWORD=$priorPassword}

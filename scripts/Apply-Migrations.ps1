param([string]$AdminUser='postgres', [string]$PostgresBin='C:\Program Files\PostgreSQL\18\bin', [switch]$EnableProfileEditing, [switch]$EnableAccounts, [switch]$EnableStoreOperations, [switch]$EnablePayments, [switch]$EnableCheckoutFirst)
$ErrorActionPreference='Stop'
$settings=Get-Content (Join-Path $env:APPDATA 'Microsoft/UserSecrets/f1743222-090c-4111-8a6c-12e064007dba/secrets.json') -Raw|ConvertFrom-Json
$connection=if($settings.'ConnectionStrings:ShelfSync'){$settings.'ConnectionStrings:ShelfSync'}else{$settings.ConnectionStrings.ShelfSync}
$parts=[System.Data.Common.DbConnectionStringBuilder]::new();$parts.set_ConnectionString($connection)
$database=[string]$parts['Database'];$appRole=[string]$parts['Username']
if($database -notin @('shelfsync_dev','shelfsync_test')){throw 'Only the named local ShelfSync demo databases are supported.'}
if($appRole -notmatch '^[a-z_][a-z0-9_]*$'){throw 'Application role requires manual administrative grant setup.'}
Write-Host "Apply additive ShelfSync migrations to $database; stock and orders are preserved."
$secure=Read-Host "PostgreSQL password for $AdminUser (not your website password)" -AsSecureString
$priorPassword=$env:PGPASSWORD
try {
 $env:PGPASSWORD=[Net.NetworkCredential]::new('',$secure).Password
 foreach($migration in Get-ChildItem (Join-Path $PSScriptRoot '../database/migrations/*.sql')|Sort-Object Name){
  if ($migration.Name -eq '005_checkout_first.sql' -and !$EnableCheckoutFirst) { continue }
  if ($migration.Name -eq '004_payments.sql' -and !$EnablePayments) { continue }
  if ($migration.Name -eq '003_store_operations.sql' -and !$EnableStoreOperations) { continue }
  if ($migration.Name -eq '002_accounts.sql' -and !$EnableAccounts) { continue }
  & "$PostgresBin/psql.exe" -w -h 127.0.0.1 -U $AdminUser -d $database -v ON_ERROR_STOP=1 -f $migration.FullName
  if($LASTEXITCODE){throw 'Migration failed.'}
 }
 & "$PostgresBin/psql.exe" -w -h 127.0.0.1 -U $AdminUser -d $database -v ON_ERROR_STOP=1 -c "GRANT SELECT ON product_profiles TO $appRole;"
 if($LASTEXITCODE){throw 'Application read permission could not be granted.'}
 if ($EnableProfileEditing) {
  & "$PostgresBin/psql.exe" -w -h 127.0.0.1 -U $AdminUser -d $database -v ON_ERROR_STOP=1 -c "GRANT INSERT, UPDATE ON product_profiles TO $appRole;"
  if ($LASTEXITCODE) { throw 'Profile edit permission grant failed.' }
 }
 if ($EnableAccounts) {
  & "$PostgresBin/psql.exe" -w -h 127.0.0.1 -U $AdminUser -d $database -v ON_ERROR_STOP=1 -c "GRANT SELECT, INSERT, UPDATE, DELETE ON app_users, app_roles, app_user_roles, app_user_claims, app_role_claims, app_user_logins, app_user_tokens TO $appRole; GRANT SELECT, INSERT ON access_audit TO $appRole; GRANT USAGE, SELECT ON SEQUENCE `"app_user_claims_Id_seq`", `"app_role_claims_Id_seq`" TO $appRole;"
  if ($LASTEXITCODE) { throw 'Account permission grant failed.' }
 }
 if ($EnableStoreOperations) {
  & "$PostgresBin/psql.exe" -w -h 127.0.0.1 -U $AdminUser -d $database -v ON_ERROR_STOP=1 -c "GRANT SELECT, INSERT ON inventory_returns, mail_preview TO $appRole;"
  if ($LASTEXITCODE) { throw 'Store operations permission grant failed.' }
 }
 if ($EnablePayments) {
  & "$PostgresBin/psql.exe" -w -h 127.0.0.1 -U $AdminUser -d $database -v ON_ERROR_STOP=1 -c "GRANT SELECT, INSERT, UPDATE ON payments TO $appRole; GRANT SELECT, INSERT ON payment_events TO $appRole;"
  if ($LASTEXITCODE) { throw 'Payment permission grant failed.' }
 }
 if ($EnableCheckoutFirst) {
  & "$PostgresBin/psql.exe" -w -h 127.0.0.1 -U $AdminUser -d $database -v ON_ERROR_STOP=1 -c "GRANT SELECT, INSERT ON payment_orders TO $appRole; GRANT SELECT ON order_payment_status TO $appRole;"
  if ($LASTEXITCODE) { throw 'Checkout-first permission grant failed.' }
 }
 Write-Host 'ShelfSync migrations completed.'     -ForegroundColor Green
} finally {$env:PGPASSWORD=$priorPassword}

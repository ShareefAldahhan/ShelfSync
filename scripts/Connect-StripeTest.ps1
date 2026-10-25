$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Write-Host 'ShelfSync Stripe TEST setup. No real payments will be initiated.' -ForegroundColor Cyan
Write-Host 'Open your Stripe sandbox API keys page privately. Paste only its sk_test_ secret key below.'
$secure=Read-Host 'Stripe test secret key (hidden input)' -AsSecureString
$key=[Net.NetworkCredential]::new('',$secure).Password
if(!$key.StartsWith('sk_test_')){throw 'Only a test secret key beginning sk_test_ is accepted.'}
$priorStripeKey=$env:STRIPE_API_KEY
try {
 $env:STRIPE_API_KEY=$key
 # Stripe uses the supplied test account; no live login/key is used. Capture the secret without printing it.
 $output=@(& stripe listen --events-from '@self' --print-secret)
 if($LASTEXITCODE){throw 'Stripe could not authenticate this test key. Check the sandbox key and try again.'}
 $hook=($output|Where-Object {$_ -match '^whsec_[A-Za-z0-9_]+$'}|Select-Object -First 1)
 if(!$hook){throw 'Stripe did not return a webhook signing secret.'}
 @{'Stripe:Enabled'='true';'Stripe:SecretKey'=$key;'Stripe:WebhookSecret'=$hook;'Stripe:BaseUrl'='http://localhost:5000'}|ConvertTo-Json|dotnet user-secrets set --project (Join-Path $root 'backend/ShelfSync.Api')|Out-Null
 if($LASTEXITCODE){throw 'Private settings could not be saved.'}
 $key=$null;$hook=$null;$output=$null
 $listener=Get-NetTCPConnection -LocalPort 5000 -State Listen -ErrorAction SilentlyContinue|Select-Object -First 1
 if($listener){
  $app=Get-CimInstance Win32_Process -Filter "ProcessId=$($listener.OwningProcess)"
  if($app.CommandLine -notmatch 'ShelfSync.Api.dll --urls http://localhost:5000'){throw 'Unexpected process on port 5000; restart ShelfSync manually.'}
  Stop-Process -Id $app.ProcessId -Force
 }
 # Only the CLI needs STRIPE_API_KEY. The app loads its own user-secrets settings.
 $cliKey=$env:STRIPE_API_KEY;$env:STRIPE_API_KEY=$null
 Start-Process pwsh -ArgumentList @('-NoProfile','-File',('"'+(Join-Path $root 'scripts/Start-Local.ps1')+'"')) -WorkingDirectory $root -WindowStyle Hidden -RedirectStandardOutput (Join-Path $root 'artifacts/stripe-test-start.log') -RedirectStandardError (Join-Path $root 'artifacts/stripe-test-error.log')
 $env:STRIPE_API_KEY=$cliKey;$cliKey=$null
 Write-Host 'Stripe test settings saved. Keep this window open for webhook forwarding.' -ForegroundColor Green
 & stripe listen --events-from '@self' --events checkout.session.completed,checkout.session.async_payment_succeeded,checkout.session.async_payment_failed,checkout.session.expired --forward-to localhost:5000/api/payments/stripe-webhook
} finally {$env:STRIPE_API_KEY=$priorStripeKey;$key=$null;$hook=$null}

param([string]$BaseUrl='http://localhost:5000')
$ErrorActionPreference='Stop'
$uri=[uri]$BaseUrl
if(!$uri.IsAbsoluteUri -or $uri.UserInfo -or $uri.Query -or $uri.Fragment -or $uri.AbsolutePath -ne '/' -or ($uri.Scheme -ne 'https' -and !($uri.Scheme -eq 'http' -and $uri.IsLoopback))){throw 'Use a fixed HTTPS origin, or HTTP loopback for local testing.'}
Write-Host 'Stripe TEST MODE only. Do not enter live keys. No charges will be initiated by this setup script.'
$secure=Read-Host 'Stripe test secret key (sk_test_...)' -AsSecureString
$key=[Net.NetworkCredential]::new('',$secure).Password
if(!$key.StartsWith('sk_test_')){throw 'Only a Stripe test secret key is allowed.'}
$secureHook=Read-Host 'Webhook signing secret from your Stripe CLI listener (whsec_...)' -AsSecureString
$hook=[Net.NetworkCredential]::new('',$secureHook).Password
if(!$hook.StartsWith('whsec_')){throw 'A webhook signing secret is required.'}
$settings=@{'Stripe:Enabled'='true';'Stripe:SecretKey'=$key;'Stripe:WebhookSecret'=$hook;'Stripe:BaseUrl'=$BaseUrl.TrimEnd('/')}
# Pipe values on stdin instead of putting secrets on a child process command line.
$settings|ConvertTo-Json|dotnet user-secrets set --id 'f1743222-090c-4111-8a6c-12e064007dba'|Out-Null
if($LASTEXITCODE){throw 'Payment configuration could not be saved.'}
$key=$null;$hook=$null;$settings=$null
Write-Host 'Stripe test settings saved privately. Keep the CLI listener running and restart ShelfSync. Real payments remain disabled.' -ForegroundColor Green

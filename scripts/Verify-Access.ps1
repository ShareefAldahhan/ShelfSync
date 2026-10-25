param([string]$PublishDirectory='artifacts/publish')
$processOptions=@{}
if($IsWindows){$processOptions.WindowStyle='Hidden'}
$ErrorActionPreference='Stop'
Push-Location (Join-Path $PSScriptRoot '..')
$server=$null
$priorName=$env:Operator__Username;$priorHash=$env:Operator__PasswordHash;$priorEnvironment=$env:ASPNETCORE_ENVIRONMENT
try {
    if(!$env:ConnectionStrings__ShelfSync -or $env:ConnectionStrings__ShelfSync -notmatch '(?i)database\s*=\s*shelfsync_test(?:;|$)'){throw 'Access verification requires the isolated shelfsync_test connection.'}
    $env:Operator__Username='access-recovery'
    $adminPassword=[Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
    $salt=[Security.Cryptography.RandomNumberGenerator]::GetBytes(16);$key=[Security.Cryptography.Rfc2898DeriveBytes]::Pbkdf2($adminPassword,$salt,100000,[Security.Cryptography.HashAlgorithmName]::SHA512,32)
    $bytes=[byte[]]::new(61);$bytes[0]=1;([byte[]](0,0,0,2,0,1,134,160,0,0,0,16)).CopyTo($bytes,1);$salt.CopyTo($bytes,13);$key.CopyTo($bytes,29)
    $env:Operator__PasswordHash=[Convert]::ToBase64String($bytes);$env:ASPNETCORE_ENVIRONMENT='Development'
    $base='http://localhost:5090';$accountPassword=[Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
    $suffix=[guid]::NewGuid().ToString('N').Substring(0,10)
    $publishedRoot=[IO.Path]::GetFullPath($PublishDirectory)
    $arguments=@(('"'+(Join-Path $publishedRoot 'ShelfSync.Api.dll')+'"'),'--contentRoot',('"'+$publishedRoot+'"'),'--urls',$base)
    function Start-AccessServer {
        $script:server=Start-Process dotnet -ArgumentList $arguments @processOptions -PassThru -RedirectStandardOutput artifacts/access-stdout.log -RedirectStandardError artifacts/access-stderr.log
        $ready=$false
        for($i=0;$i -lt 30;$i++){try{$null=Invoke-RestMethod "$base/health/live" -TimeoutSec 2;$ready=$true;break}catch{Start-Sleep -Milliseconds 500}}
        if(!$ready){throw 'Account verification server did not start.'}
    }
    function Restart-AccessServer {Stop-Process -Id $script:server.Id -Force;$script:server.WaitForExit();Start-AccessServer}
    function Anonymous-Client {
        $state=Invoke-RestMethod "$base/api/auth/session" -SessionVariable browser
        return @{Browser=$browser;Token=$state.token}
    }
    function Request($client,$method,$path,$payload,$expected) {
        $options=@{Uri="$base$path";Method=$method;WebSession=$client.Browser;Headers=@{'X-CSRF-TOKEN'=$client.Token;'Idempotency-Key'=[guid]::NewGuid().ToString()};SkipHttpErrorCheck=$true}
        if($null -ne $payload){$options.ContentType='application/json';$options.Body=$payload|ConvertTo-Json -Depth 6}
        $response=Invoke-WebRequest @options
        if($response.StatusCode -ne $expected){throw "$method $path returned HTTP $($response.StatusCode); expected $expected."}
        if($response.Content){return $response.Content|ConvertFrom-Json}
    }
    function Login($name,$password) {
        $client=Anonymous-Client
        $null=Request $client 'POST' '/api/auth/login' @{username=$name;password=$password} 200
        $state=Invoke-RestMethod "$base/api/auth/session" -WebSession $client.Browser
        $client.Token=$state.token;$client.Roles=$state.roles;$client.Id=$state.userId;return $client
    }
    Start-AccessServer
    $admin=Login 'access-recovery' $adminPassword
    $assocName="associate-$suffix";$managerName="manager-$suffix";$aName="customer-a-$suffix";$bName="customer-b-$suffix"
    $associateRecord=Request $admin 'POST' '/api/admin/users' @{username=$assocName;password=$accountPassword;role='Associate'} 201
    $null=Request $admin 'POST' '/api/admin/users' @{username=$managerName;password=$accountPassword;role='Manager'} 201
    $null=Request $admin 'POST' '/api/admin/users' @{username=$aName;password=$accountPassword;role='Customer'} 201
    $null=Request $admin 'POST' '/api/admin/users' @{username=$bName;password=$accountPassword;role='Customer'} 201
    $associate=Login $assocName $accountPassword;$manager=Login $managerName $accountPassword;$a=Login $aName $accountPassword;$b=Login $bName $accountPassword
    $products=@(Request $a 'GET' '/api/products' $null 200);$product=$products[0].id
    $null=Request $associate 'POST' '/api/reservations' @{productId=$product;quantity=1} 403
    $null=Request $associate 'POST' '/api/cart/reserve' @{items=@(@{productId=$product;quantity=1})} 403
    $null=Request $associate 'POST' "/api/payments/orders/$([guid]::NewGuid())/checkout" @{} 403

    $null=Request $a 'GET' "/api/products/$product/details" $null 403
    $null=Request $a 'POST' '/api/pos/sales' @{eventId=[guid]::NewGuid();productId=$product;quantity=1} 403
    $null=Request $a 'POST' "/api/products/$product/location" @{bin='UNAUTHORIZED';reason='Must be rejected'} 403
    $null=Request $a 'POST' "/api/products/$product/stock-correction" @{shelfUnits=0;expectedShelfUnits=0;reason='Must be rejected'} 403
    $null=Request $a 'GET' '/api/admin/users' $null 403
    $null=Request $associate 'POST' "/api/products/$product/stock-correction" @{shelfUnits=0;expectedShelfUnits=0;reason='Must be rejected'} 403
    $null=Request $associate 'POST' "/api/orders/$([guid]::NewGuid())/resolve-shortage" @{} 403
    $null=Request $associate 'GET' '/api/admin/users' $null 403
    $null=Request $manager 'GET' '/api/admin/users' $null 403
    $null=Request $associate 'GET' "/api/products/$product/details" $null 200
    $null=Request $associate 'POST' "/api/products/$product/location" @{aisle='A03';bay='02';shelf='01';bin='ACCESS-TEST';reason='Authorized location test'} 200
    $stock=Request $manager 'GET' "/api/products/$product/details" $null 200
    $null=Request $manager 'POST' "/api/products/$product/stock-correction" @{shelfUnits=$stock.stock.shelf;expectedShelfUnits=$stock.stock.shelf;reason='Authorized manager recount'} 200
    $own=Request $a 'POST' '/api/reservations' @{productId=$product;quantity=1;ownerUserId=$b.Id} 201
    $other=Request $b 'POST' '/api/reservations' @{productId=$product;quantity=1} 201
    $staffReservation=Request $admin 'POST' '/api/reservations' @{productId=$product;quantity=1} 201
    $null=Request $associate 'POST' "/api/orders/$($staffReservation.orderId)/pick" @{} 409
    $staffOrders=@(Request $associate 'GET' '/api/orders' $null 200)
    if(($staffOrders|Where-Object id -eq $staffReservation.orderId).customerReservation){throw 'Staff reservation incorrectly marked as a customer reservation.'}
    if(!($staffOrders|Where-Object id -eq $own.orderId).customerReservation){throw 'Customer reservation missing picking eligibility.'}
    $null=Request $admin 'POST' "/api/reservations/$($staffReservation.orderId)/cancel" @{} 200

    $mine=@(Request $a 'GET' '/api/orders' $null 200);if($mine.Count -ne 1 -or $mine[0].id -ne $own.orderId){throw 'Customer order list leaked another account or legacy order.'}
    $null=Request $b 'POST' "/api/reservations/$($own.orderId)/cancel" @{} 404
    $null=Request $a 'POST' "/api/orders/$($own.orderId)/pick" @{} 403
    $null=Request $a 'POST' "/api/reservations/$($own.orderId)/cancel" @{} 200
    $null=Request $b 'POST' "/api/reservations/$($other.orderId)/cancel" @{} 200
    $null=Request $admin 'POST' "/api/admin/users/$($associateRecord.id)/access" @{role='Associate';enabled=$false;reason='Disable test account'} 200
    $null=Request $associate 'GET' '/api/products' $null 401
    $null=Request $admin 'POST' "/api/admin/users/$($associateRecord.id)/access" @{role='Manager';enabled=$true;reason='Change role test'} 200
    $null=Request $admin 'POST' "/api/admin/users/$($admin.Id)/access" @{role='Customer';enabled=$false;reason='Must not remove recovery admin'} 409
    $list=Request $admin 'GET' '/api/admin/users' $null 200
    if(($list|ConvertTo-Json -Depth 8) -match 'passwordHash|securityStamp'){throw 'Account list exposed credential fields.'}
    Restart-AccessServer
    $promoted=Login $assocName $accountPassword;if($promoted.Roles -notcontains 'Manager'){throw 'Changed account role did not take effect.'}
    $signup=Anonymous-Client
    $null=Request $signup 'POST' '/api/auth/register' @{username="registered-$suffix";password=$accountPassword;role='Admin'} 200
    $registered=Invoke-RestMethod "$base/api/auth/session" -WebSession $signup.Browser
    if($registered.roles.Count -ne 1 -or $registered.roles[0] -ne 'Customer'){throw 'Registration allowed privilege escalation.'}
    $signup.Token=$registered.token;$null=Request $signup 'GET' '/api/admin/users' $null 403
    $null=Request $admin 'POST' "/api/admin/users/$($associateRecord.id)/password" @{password=($accountPassword+'New');reason='Reset credential test'} 200
    $null=Request $promoted 'GET' '/api/products' $null 401
    $null=Request $b 'POST' '/api/account/password' @{currentPassword=$accountPassword;newPassword=($accountPassword+'Changed')} 200
    $null=Request $b 'GET' '/api/products' $null 401
    Restart-AccessServer
    $locked=Anonymous-Client
    for($attempt=0;$attempt -lt 5;$attempt++){$null=Request $locked 'POST' '/api/auth/login' @{username=$aName;password='Wrong-password-test'} 401}
    Restart-AccessServer
    $locked=Anonymous-Client;$null=Request $locked 'POST' '/api/auth/login' @{username=$aName;password=$accountPassword} 401
    Restart-AccessServer
    $accounts=@(Request $admin 'GET' '/api/admin/users' $null 200)
    $aRecord=$accounts|Where-Object username -eq $aName
    $bRecord=$accounts|Where-Object username -eq $bName
    $null=Request $signup 'POST' "/api/admin/users/$($aRecord.id)/recovery-code" @{reason='Forbidden customer request'} 403
    $null=Request $admin 'POST' "/api/admin/users/$($admin.Id)/recovery-code" @{reason='Protected administrator'} 409
    $first=Request $admin 'POST' "/api/admin/users/$($aRecord.id)/recovery-code" @{reason='Verified identity in isolated recovery test'} 200
    $code=Request $admin 'POST' "/api/admin/users/$($aRecord.id)/recovery-code" @{reason='Replace previous recovery code'} 200
    if($code.expiresInMinutes -ne 30){throw 'Unexpected recovery expiry.'}
    $recover=Anonymous-Client
    $null=Request $recover 'POST' '/api/auth/reset-password' @{username=$aName;code=$first.code;newPassword=($accountPassword+'Recovered')} 400
    $null=Request $recover 'POST' '/api/auth/reset-password' @{username=$bName;code=$code.code;newPassword=($accountPassword+'Recovered')} 400
    $null=Request $recover 'POST' '/api/auth/reset-password' @{username=$aName;code=($code.code+'tampered');newPassword=($accountPassword+'Recovered')} 400
    $null=Request $recover 'POST' '/api/auth/reset-password' @{username='nonexistent-account';code=$code.code;newPassword=($accountPassword+'Recovered')} 400
    $null=Request $recover 'POST' '/api/auth/reset-password' @{username=$aName;code=$code.code;newPassword=($accountPassword+'Recovered')} 200
    Restart-AccessServer
    $recover=Anonymous-Client
    $null=Request $recover 'POST' '/api/auth/reset-password' @{username=$aName;code=$code.code;newPassword=($accountPassword+'Again')} 400
    $recovered=Login $aName ($accountPassword+'Recovered')
    $unused=Request $admin 'POST' "/api/admin/users/$($aRecord.id)/recovery-code" @{reason='Verify session revocation'} 200
    $null=Request $recovered 'GET' '/api/products' $null 401
    $null=Request $admin 'POST' "/api/admin/users/$($bRecord.id)/access" @{role='Customer';enabled=$false;reason='Disabled recovery test'} 200
    $null=Request $admin 'POST' "/api/admin/users/$($bRecord.id)/recovery-code" @{reason='Disabled recovery must fail'} 409
    $audit=@(Request $admin 'GET' '/api/admin/audit' $null 200)
    if(!($audit|Where-Object action -eq 'RECOVER_PASSWORD')){throw 'Recovery audit missing.'}
    if(($audit|ConvertTo-Json -Depth 8).Contains($code.code)){throw 'Recovery token leaked into audit.'}
    Write-Output 'PASS: administrator-issued recovery; replaced/tampered/wrong-user/replayed codes rejected; locked account recovered; disabled and bootstrap accounts protected; session revocation and secret-free audit.'
    Restart-AccessServer
    $null=Request $signup 'GET' '/api/stock' $null 403
    $null=Request $signup 'GET' '/api/inventory-history' $null 403
    $null=Request $signup 'GET' '/api/admin/mail-preview' $null 403
    $null=Request $signup 'POST' '/api/stock/receive' @{productId=$product;quantity=1;reason='Forbidden customer action'} 403
    $null=Request $signup 'POST' '/api/returns' @{saleId=[guid]::NewGuid();isPos=$false;quantity=1;destination='backroom';reason='Forbidden customer action'} 403
    $null=Request $manager 'GET' '/api/stock' $null 200
    $null=Request $manager 'POST' '/api/stock/receive' @{productId=$product;quantity=5;reason='Published delivery test';reference='PO-HTTP'} 200
    $null=Request $manager 'POST' '/api/stock/replenish' @{productId=$product;quantity=2;reason='Published shelf replenishment'} 200
    $cart=Request $signup 'POST' '/api/cart/reserve' @{items=@(@{productId=$product;quantity=1})} 201
    $cartOrder=$cart.orders[0].orderId
    $receipt=Request $signup 'GET' "/api/orders/$cartOrder/receipt" $null 200
    if($receipt.cartId -ne $cart.cartId){throw 'Receipt lost pickup group.'}
    $null=Request $admin 'POST' '/api/admin/users' @{username="receipt-other-$suffix";password=$accountPassword;role='Customer'} 201
    $receiptOther=Login "receipt-other-$suffix" $accountPassword
    $null=Request $receiptOther 'GET' "/api/orders/$cartOrder/receipt" $null 404
    $null=Request $manager 'POST' "/api/orders/$cartOrder/pick" @{} 200
    $null=Request $manager 'POST' "/api/orders/$cartOrder/cancel-picked" @{destination='backroom';reason='Published cancellation and restock'} 200
    $null=Request $manager 'GET' '/api/inventory-history?search=PO-HTTP&page=1' $null 200
    $null=Request $signup 'GET' '/api/orders?page=2' $null 200
    $null=Request $signup 'GET' '/api/orders?page=0' $null 400
    $forgot=Anonymous-Client
    $eligible=Request $forgot 'POST' '/api/auth/forgot-password' @{username="registered-$suffix"} 200
    $unknown=Request $forgot 'POST' '/api/auth/forgot-password' @{username='no-such-account'} 200
    if($eligible.message -ne $unknown.message){throw 'Recovery request disclosed account existence.'}
    $mail=@(Request $admin 'GET' '/api/admin/mail-preview' $null 200)
    if(!($mail|Where-Object subject -eq 'Password recovery') -or !($mail|Where-Object subject -eq 'Order CANCELLED')){throw 'Local recovery or order notification preview missing.'}
    Write-Output 'PASS: published receiving/replenishment, stock and history permissions, atomic cart endpoint, owned receipt, picked cancellation, pagination and administrator-only local email preview.'
    $config=Request $signup 'GET' '/api/payments/config' $null 200
    if($config.enabled -or !$config.testMode){throw 'Isolated verification must not enable external payment calls.'}
    $null=Request $signup 'GET' '/api/payments/admin/recent' $null 403
    $null=Request $admin 'GET' '/api/payments/admin/recent' $null 200
    $null=Request $signup 'GET' "/api/payments/orders/$cartOrder" $null 200
    $null=Request $receiptOther 'GET' "/api/payments/orders/$cartOrder" $null 404
    $null=Request $signup 'POST' "/api/payments/orders/$cartOrder/checkout" @{} 503
    Write-Output 'PASS: payment endpoints fail closed without provider configuration, expose test mode, enforce ownership and protect administrative payment records.'
    Write-Output 'PASS: four-role API matrix; customer order ownership and payload spoofing; disabled/changed-role sessions; protected recovery admin; customer-only registration; admin reset; own password change; persistent account lockout; no credential fields in account lists.'
} finally {
    if($server -and !$server.HasExited){Stop-Process -Id $server.Id -Force}
    $env:Operator__Username=$priorName;$env:Operator__PasswordHash=$priorHash;$env:ASPNETCORE_ENVIRONMENT=$priorEnvironment
    Pop-Location
}

param([switch]$Published)
# Runs the real API with temporary credentials. Requires the normal local DB user secret.
$ErrorActionPreference = 'Stop'
Push-Location (Join-Path $PSScriptRoot '..')
$server = $null
try {
    $password = [Convert]::ToBase64String([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
    $salt = [System.Security.Cryptography.RandomNumberGenerator]::GetBytes(16)
    $subkey = [System.Security.Cryptography.Rfc2898DeriveBytes]::Pbkdf2($password, $salt, 100000, [System.Security.Cryptography.HashAlgorithmName]::SHA512, 32)
    $bytes = [byte[]]::new(61); $bytes[0]=1
    ([byte[]](0,0,0,2,0,1,134,160,0,0,0,16)).CopyTo($bytes,1)
    $salt.CopyTo($bytes,13);$subkey.CopyTo($bytes,29)
    $env:Operator__PasswordHash = [Convert]::ToBase64String($bytes)
    $env:Operator__Username = 'smoke-operator'
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    New-Item -ItemType Directory -Force artifacts | Out-Null
    $arguments = if ($Published) { @('artifacts/publish/ShelfSync.Api.dll','--contentRoot', ('"' + (Join-Path (Get-Location) 'artifacts/publish') + '"'),'--urls','http://localhost:5088') } else { @('run','--no-build','--no-launch-profile','--project','backend/ShelfSync.Api','--urls','http://localhost:5088') }
    $server = Start-Process dotnet -ArgumentList $arguments -WindowStyle Hidden -PassThru -RedirectStandardOutput artifacts/http-stdout.log -RedirectStandardError artifacts/http-stderr.log
    $base='http://localhost:5088'
    for ($i=0; $i -lt 30; $i++) {
        try { $null=Invoke-RestMethod "$base/health/live"; break } catch { Start-Sleep -Milliseconds 500 }
    }
    if ($Published) {
        $page=Invoke-WebRequest $base
        if ($page.Content -notmatch '<title>ShelfSync</title>') { throw 'Published dashboard missing.' }
    }
    $r=Invoke-WebRequest "$base/api/products" -SkipHttpErrorCheck
    if ($r.StatusCode -ne 401) { throw 'Anonymous API access was not denied.' }
    $s=Invoke-RestMethod "$base/api/auth/session" -SessionVariable browser
    $login=@{username='smoke-operator';password=$password}|ConvertTo-Json
    $r=Invoke-WebRequest "$base/api/auth/login" -Method Post -WebSession $browser -ContentType application/json -Body $login -SkipHttpErrorCheck
    if ($r.StatusCode -ne 400) { throw 'Missing CSRF token was not rejected.' }
    $null=Invoke-RestMethod "$base/api/auth/login" -Method Post -WebSession $browser -Headers @{'X-CSRF-TOKEN'=$s.token} -ContentType application/json -Body $login
    $s=Invoke-RestMethod "$base/api/auth/session" -WebSession $browser
    if (!$s.authenticated) { throw 'Authentication did not persist.' }
    $products=Invoke-RestMethod "$base/api/products" -WebSession $browser
    $orders=Invoke-RestMethod "$base/api/orders" -WebSession $browser
    if ($env:SHELFSYNC_SMOKE_PRODUCT) {
        function Post-Operation($path, $payload, $key) {
            $headers=@{'X-CSRF-TOKEN'=$s.token; 'Idempotency-Key'=$key}
            $json=if ($payload) { $payload | ConvertTo-Json } else { '{}' }
            Invoke-RestMethod "$base$path" -Method Post -WebSession $browser -Headers $headers -ContentType application/json -Body $json
        }
        $product=$env:SHELFSYNC_SMOKE_PRODUCT
        $key=[guid]::NewGuid().ToString()
        $reserved=Post-Operation '/api/reservations' @{productId=$product;quantity=1} $key
        $replay=Post-Operation '/api/reservations' @{productId=$product;quantity=1} $key
        if ($reserved.orderId -ne $replay.orderId) { throw 'Reservation replay duplicated order.' }
        $cancelled=Post-Operation "/api/reservations/$($reserved.orderId)/cancel" $null ([guid]::NewGuid().ToString())
        if ($cancelled.state -ne 'CANCELLED') { throw 'Cancel failed.' }
        $reserved=Post-Operation '/api/reservations' @{productId=$product;quantity=1} ([guid]::NewGuid().ToString())
        $picked=Post-Operation "/api/orders/$($reserved.orderId)/pick" $null ([guid]::NewGuid().ToString())
        if ($picked.state -ne 'PICKED') { throw 'Pick failed.' }
        $fulfilled=Post-Operation "/api/orders/$($reserved.orderId)/fulfill" $null ([guid]::NewGuid().ToString())
        if ($fulfilled.state -ne 'FULFILLED') { throw 'Fulfill failed.' }
        $reserved=Post-Operation '/api/reservations' @{productId=$product;quantity=1} ([guid]::NewGuid().ToString())
        $shortage=Post-Operation "/api/orders/$($reserved.orderId)/shortage" $null ([guid]::NewGuid().ToString())
        if ($shortage.state -ne 'SHORTAGE_PENDING') { throw 'Shortage failed.' }
        $resolved=Post-Operation "/api/orders/$($reserved.orderId)/resolve-shortage" $null ([guid]::NewGuid().ToString())
        if ($resolved.state -ne 'RESERVED') { throw 'Resolution failed.' }
        $null=Post-Operation "/api/reservations/$($reserved.orderId)/cancel" $null ([guid]::NewGuid().ToString())
        $event=[guid]::NewGuid().ToString()
        $null=Post-Operation '/api/pos/sales' @{eventId=$event;productId=$product;quantity=1} $event
        $null=Post-Operation '/api/pos/sales' @{eventId=$event;productId=$product;quantity=1} $event
        $stock=Invoke-RestMethod "$base/api/products/$product" -WebSession $browser
        if ($stock.shelfUnits -ne 8 -or $stock.pickedUnits -ne 0) { throw 'Final stock invariant failed.' }
        Write-Output 'PASS: reserve/replay/cancel, reserve/pick/fulfill, shortage/resolve/cancel, POS/replay, final inventory.'
    }
    $null = $browser.Headers.Remove('Idempotency-Key')
    $r=Invoke-WebRequest "$base/api/reservations" -Method Post -WebSession $browser -Headers @{'X-CSRF-TOKEN'=$s.token} -ContentType application/json -Body '{"productId":"00000000-0000-4000-8000-000000000001","quantity":1}' -SkipHttpErrorCheck
    if ($r.StatusCode -ne 400) { throw "Missing idempotency key was not rejected (HTTP $($r.StatusCode))." }
    $null=Invoke-RestMethod "$base/api/auth/logout" -Method Post -WebSession $browser -Headers @{'X-CSRF-TOKEN'=$s.token}
    $r=Invoke-WebRequest "$base/api/orders" -WebSession $browser -SkipHttpErrorCheck
    if ($r.StatusCode -ne 401) { throw 'Logout did not revoke browser access.' }
    Write-Output "PASS: anonymous denial, CSRF denial, login, session, catalog ($($products.Count)), orders ($($orders.Count)), idempotency validation, logout."
} finally {
    if ($server -and !$server.HasExited) { Stop-Process -Id $server.Id -Force }
    $env:Operator__PasswordHash=$null;$env:Operator__Username=$null;$env:ASPNETCORE_ENVIRONMENT=$null
    Pop-Location
}

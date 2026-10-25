param([string]$Username = 'operator')
$ErrorActionPreference = 'Stop'
do {
    $securePassword = Read-Host 'Choose an operator password (12 to 1024 characters)' -AsSecureString
    $password = [System.Net.NetworkCredential]::new('', $securePassword).Password
    $validLength = $password.Length -ge 12 -and $password.Length -le 1024
    if (!$validLength) { Write-Host 'Please try again with 12 to 1024 characters.' -ForegroundColor Yellow }
} until ($validLength)
# ASP.NET Identity V3 format: marker, PRF SHA512, iterations, salt length, salt, subkey.
$salt = [System.Security.Cryptography.RandomNumberGenerator]::GetBytes(16)
$subkey = [System.Security.Cryptography.Rfc2898DeriveBytes]::Pbkdf2($password, $salt, 100000, [System.Security.Cryptography.HashAlgorithmName]::SHA512, 32)
$bytes = [byte[]]::new(61)
$bytes[0] = 1
([byte[]](0,0,0,2,0,1,134,160,0,0,0,16)).CopyTo($bytes,1)
$salt.CopyTo($bytes,13); $subkey.CopyTo($bytes,29)
$hash = [Convert]::ToBase64String($bytes)
@{'Operator:Username'=$Username;'Operator:PasswordHash'=$hash}|ConvertTo-Json|dotnet user-secrets set --id 'f1743222-090c-4111-8a6c-12e064007dba'|Out-Null
if($LASTEXITCODE){throw 'Operator configuration could not be saved.'}
$password = $null
Write-Output 'Operator credentials configured securely in local user secrets.'

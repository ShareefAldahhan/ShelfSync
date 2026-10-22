param([string]$Username = 'operator')
$ErrorActionPreference = 'Stop'
$securePassword = Read-Host 'Choose an operator password (at least 12 characters)' -AsSecureString
$password = [System.Net.NetworkCredential]::new('', $securePassword).Password
if ($password.Length -lt 12) { throw 'Use at least 12 characters.' }
# ASP.NET Identity V3 format: marker, PRF SHA512, iterations, salt length, salt, subkey.
$salt = [System.Security.Cryptography.RandomNumberGenerator]::GetBytes(16)
$subkey = [System.Security.Cryptography.Rfc2898DeriveBytes]::Pbkdf2($password, $salt, 100000, [System.Security.Cryptography.HashAlgorithmName]::SHA512, 32)
$bytes = [byte[]]::new(61)
$bytes[0] = 1
([byte[]](0,0,0,2,0,1,134,160,0,0,0,16)).CopyTo($bytes,1)
$salt.CopyTo($bytes,13); $subkey.CopyTo($bytes,29)
$hash = [Convert]::ToBase64String($bytes)
$project = Join-Path $PSScriptRoot '../backend/ShelfSync.Api'
dotnet user-secrets set 'Operator:Username' $Username --project $project | Out-Null
dotnet user-secrets set 'Operator:PasswordHash' $hash --project $project | Out-Null
$password = $null
Write-Output 'Operator credentials configured securely in local user secrets.'

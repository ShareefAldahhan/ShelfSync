#Requires -Version 7.0
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$fixture=Join-Path $root ('artifacts/package-test-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
$payload=Join-Path $fixture 'payload.txt'
Set-Content -LiteralPath $payload -Value 'original'
$manifest=@{algorithm='SHA256';files=@(@{path='payload.txt';sha256=(Get-FileHash $payload).Hash})}
$manifest|ConvertTo-Json -Depth 4|Set-Content (Join-Path $fixture 'manifest.json')
$verifier=Join-Path $PSScriptRoot 'Test-Package.ps1'
& $verifier -PackageRoot $fixture
function Assert-Rejected([string]$Case){
    $rejected=$false
    try { & $verifier -PackageRoot $fixture | Out-Null } catch { $rejected=$true }
    if(!$rejected){throw "Package verification accepted $Case."}
}
Set-Content -LiteralPath $payload -Value 'changed'
Assert-Rejected 'a changed file'
Set-Content -LiteralPath $payload -Value 'original'
$extra=Join-Path $fixture 'unexpected.txt'
Set-Content -LiteralPath $extra -Value 'extra'
Assert-Rejected 'an unexpected file'
Remove-Item -LiteralPath $extra
Remove-Item -LiteralPath $payload
Assert-Rejected 'a missing file'
$manifest.files[0].path='../outside.txt'
$manifest|ConvertTo-Json -Depth 4|Set-Content (Join-Path $fixture 'manifest.json')
Assert-Rejected 'a path outside the package'
Write-Output 'PASS: package verifier accepts intact files and rejects changed, unexpected, missing and escaping paths.'

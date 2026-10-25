param([string]$PackageRoot=(Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath($PackageRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
$manifest=Get-Content (Join-Path $root 'manifest.json') -Raw | ConvertFrom-Json
$expected=@{}
foreach($entry in $manifest.files){
    $path=[IO.Path]::GetFullPath((Join-Path $root $entry.path))
    if(!$path.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Invalid package manifest path.'}
    if($expected.ContainsKey($entry.path)){throw 'Duplicate package manifest entry.'}
    $expected[$entry.path]=$true
    if(!(Test-Path -LiteralPath $path -PathType Leaf)){throw "Package file missing: $($entry.path)"}
    if((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $entry.sha256){throw "Package file changed: $($entry.path)"}
}
foreach($file in Get-ChildItem -LiteralPath $root -File -Recurse){
    $relative=[IO.Path]::GetRelativePath($root,$file.FullName).Replace('\','/')
    if($relative -ne 'manifest.json' -and !$expected.ContainsKey($relative)){throw "Unexpected package file: $relative"}
}
Write-Output "PASS: $($manifest.files.Count) package files match the manifest."

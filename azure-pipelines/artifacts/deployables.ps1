$RepoRoot = [System.IO.Path]::GetFullPath("$PSScriptRoot/../..")
$BuildConfiguration = $env:BUILDCONFIGURATION
if (!$BuildConfiguration) {
    $BuildConfiguration = 'Debug'
}

$result = @{ }

$PackagesRoot = "$RepoRoot/bin/Packages/$BuildConfiguration"
if (Test-Path $PackagesRoot) {
    $result[$PackagesRoot] = (Get-ChildItem $PackagesRoot -Recurse)
}

$SosThreadingToolsRoot = "$RepoRoot/bin/SosThreadingTools/$BuildConfiguration/net472"
if (Test-Path $SosThreadingToolsRoot) {
    $result[$SosThreadingToolsRoot] = (Get-ChildItem "$SosThreadingToolsRoot/SosThreadingTools.dll" -Recurse)
}

$result
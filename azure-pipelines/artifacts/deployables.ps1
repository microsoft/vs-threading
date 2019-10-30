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

if (Test-Path "$RepoRoot/bin/SosThreadingTools") {
    $result["$RepoRoot/bin/SosThreadingTools/x86/$BuildConfiguration/net472"] = "$RepoRoot/bin/SosThreadingTools/x86/$BuildConfiguration/net472/SosThreadingTools_x86.dll";
    $result["$RepoRoot/bin/SosThreadingTools/x64/$BuildConfiguration/net472"] = "$RepoRoot/bin/SosThreadingTools/x64/$BuildConfiguration/net472/SosThreadingTools_x64.dll";
}

$result
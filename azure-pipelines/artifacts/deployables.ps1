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
    $ArchivePath = "$RepoRoot\obj\SosThreadingTools\SosThreadingTools.zip"
    $ArchiveLayout = "$RepoRoot\obj\SosThreadingTools\ArchiveLayout"
    if (Test-Path $ArchiveLayout) { Remove-Item -Force $ArchiveLayout -Recurse }
    New-Item -Path $ArchiveLayout -ItemType Directory | Out-Null
    Copy-Item -Force -Path "$SosThreadingToolsRoot" -Recurse  -Exclude "DllExport.dll","*.xml" -Destination $ArchiveLayout
    Rename-Item -Path $ArchiveLayout\net472 $ArchiveLayout\SosThreadingTools
    Get-ChildItem -Path $ArchiveLayout\symstore -Recurse | Remove-Item
    Compress-Archive -Force -Path $ArchiveLayout\SosThreadingTools -DestinationPath $ArchivePath

    $result[(Split-Path $ArchivePath -Parent)] = $ArchivePath
}

$result

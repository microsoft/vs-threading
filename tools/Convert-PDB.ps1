<#
.SYNOPSIS
    Converts between Windows PDB and Portable PDB formats.
.PARAMETER DllPath
    The path to the DLL whose PDB is to be converted.
.PARAMETER PdbPath
    The path to the PDB to convert. May be omitted if the DLL was compiled on this machine and the PDB is still at its original path.
.PARAMETER OutputPath
    The path of the output PDB to write.
#>
[CmdletBinding()]
Param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$DllPath,
    [Parameter()]
    [string]$PdbPath,
    [Parameter(Mandatory = $true, Position = 1)]
    [string]$OutputPath
)

if ($IsMacOS -or $IsLinux) {
    Write-Error "This script only works on Windows"
    return
}

# This package originally comes from the https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-tools/nuget/v3/index.json feed.
# Add this feed as an upstream to whatever feed is in nuget.config if this step fails.
$packageID = 'Microsoft.DiaSymReader.Pdb2Pdb'
$packageVersion = '1.1.0-beta2-21101-01'
try {
    $pdb2pdbpath = & "$PSScriptRoot/Download-NuGetPackage.ps1" -PackageId $packageID -Version $packageVersion -Source https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-tools/nuget/v3/index.json
}
catch {
    Write-Error "Failed to install $packageID. Consider adding https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-tools/nuget/v3/index.json as an upstream to your nuget.config feed."
    return
}

$outputDirectory = Split-Path $OutputPath -Parent
if ($outputDirectory) {
    New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
}

$toolpath = "$pdb2pdbpath/tools/Pdb2Pdb.exe"
$arguments = $DllPath, '/out', $OutputPath, '/nowarn', '0021'
if ($PdbPath) {
    $arguments += '/pdb', $PdbPath
}

Write-Verbose "$toolpath $arguments"
& $toolpath $arguments

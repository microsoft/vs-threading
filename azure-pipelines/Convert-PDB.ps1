<#
.SYNOPSIS
    Builds all projects in this repo.
.PARAMETER DllPath
    The path to the DLL whose PDB is to be converted.
.PARAMETER PdbPath
    The path to the PDB to convert. May be omitted if the DLL was compiled on this machine and the PDB is still at its original path.
.PARAMETER OutputPath
    The path of the output PDB to write.
#>
#Function Convert-PortableToWindowsPDB() {
    Param(
        [Parameter(Mandatory=$true,Position=0)]
        [string]$DllPath,
        [Parameter()]
        [string]$PdbPath,
        [Parameter(Mandatory=$true,Position=1)]
        [string]$OutputPath
    )

    $version = '1.1.0-beta1-63314-01'
    $pdb2pdbpath = "$env:temp\pdb2pdb.$version\tools\Pdb2Pdb.exe"
    if (-not (Test-Path $pdb2pdbpath)) {
        nuget install pdb2pdb -version $version -PackageSaveMode nuspec -OutputDirectory $env:temp -Source https://dotnet.myget.org/F/symreader-converter/api/v3/index.json
    }

    $args = $DllPath,'/out',$OutputPath,'/nowarn','0021'
    if ($PdbPath) {
        $args += '/pdb',$PdbPath
    }

    & "$env:temp\pdb2pdb.$version\tools\Pdb2Pdb.exe" $args
#}

<#
.SYNOPSIS
    Downloads the NuGet.exe tool and returns the path to it.
#>

function Expand-ZIPFile($file, $destination) {
    if (!(Test-Path $destination)) { $null = mkdir $destination }
    $shell = new-object -com shell.application
    $zip = $shell.NameSpace((Resolve-Path $file).Path)
    foreach ($item in $zip.items()) {
        $shell.Namespace((Resolve-Path $destination).Path).copyhere($item)
    }
}

$binaryToolsPath = "$PSScriptRoot\..\obj\tools"
if (!(Test-Path $binaryToolsPath)) { $null = mkdir $binaryToolsPath }
$nugetPath = "$binaryToolsPath\nuget.exe"
if (!(Test-Path $nugetPath)) {
    Write-Host "Downloading NuGet credential helper..." -ForegroundColor Yellow
    $bundleZipPath = "$binaryToolsPath\CredentialProviderBundle.zip"
    if (!(Test-Path $bundleZipPath)) {
        Invoke-WebRequest -Uri "https://devdiv.pkgs.visualstudio.com/_apis/public/nuget/client/CredentialProviderBundle.zip" -OutFile $bundleZipPath 
    }

    $bundleDir = "$binaryToolsPath\CredentialProviderBundle"
    Expand-ZIPFile $bundleZipPath $bundleDir
    Copy-Item $bundleDir\nuget.exe $binaryToolsPath
    Copy-Item $bundleDir\CredentialProvider.VSS.exe $binaryToolsPath

    # Replace the nuget.exe that came in the bundle with the version we want to use. 
    $NuGetVersion = "3.3.0"
    Write-Host "Downloading nuget.exe $NuGetVersion..." -ForegroundColor Yellow
    Invoke-WebRequest -Uri "https://dist.nuget.org/win-x86-commandline/v$NuGetVersion/nuget.exe" -OutFile $nugetPath
}

$nugetPath

<#
.SYNOPSIS
    Downloads the NuGet.exe tool and returns the path to it.
#>

$binaryToolsPath = "$PSScriptRoot\..\obj\tools"
if (!(Test-Path $binaryToolsPath)) { $null = mkdir $binaryToolsPath }
$nugetPath = "$binaryToolsPath\nuget.exe"
if (!(Test-Path $nugetPath)) {
    $NuGetVersion = "3.3.0"
    Write-Host "Downloading nuget.exe $NuGetVersion..." -ForegroundColor Yellow
    Invoke-WebRequest -Uri "https://dist.nuget.org/win-x86-commandline/v$NuGetVersion/nuget.exe" -OutFile $nugetPath
}

$nugetPath

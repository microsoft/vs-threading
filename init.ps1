<#
.SYNOPSIS
    Restores NuGet packages.
.PARAMETER Signing
    Install the MicroBuild signing plugin for building test-signed builds on desktop machines.
.PARAMETER IBCMerge
    Install the MicroBuild IBCMerge plugin for building optimized assemblies on desktop machines.
#>
Param(
    [Parameter()]
    [switch]$Signing,
    [Parameter()]
    [switch]$IBCMerge
)

Push-Location $PSScriptRoot
try {
    $HeaderColor = 'Green'
    $toolsPath = "$PSScriptRoot\tools"
    $nugetVerbosity = 'quiet'
    if ($Verbose) { $nugetVerbosity = 'normal' }

    # First restore NuProj packages since the solution restore depends on NuProj evaluation succeeding.
    gci "$PSScriptRoot\src\project.json" -rec |? { $_.FullName -imatch 'nuget' } |% {
        & "$toolsPath\Restore-NuGetPackages.ps1" -Path $_ -Verbosity $nugetVerbosity
    }

    # Restore test assets
    & "$toolsPath\Restore-NuGetPackages.ps1" -Path "$PSScriptRoot\src\Microsoft.VisualStudio.Threading.Analyzers.TestAssets\project.json"  -Verbosity $nugetVerbosity

    # Restore VS solution dependencies
    gci "$PSScriptRoot\src" -rec |? { $_.FullName.EndsWith('.sln') } |% {
        & "$toolsPath\Restore-NuGetPackages.ps1" -Path $_.FullName -Verbosity $nugetVerbosity
    }

    $MicroBuildPackageSource = 'https://devdiv.pkgs.visualstudio.com/DefaultCollection/_packaging/MicroBuildToolset/nuget/v3/index.json'
    if ($Signing) {
        Write-Host "Installing MicroBuild signing plugin" -ForegroundColor $HeaderColor
        & "$toolsPath\Install-NuGetPackage.ps1" MicroBuild.Plugins.Signing -source $MicroBuildPackageSource -Verbosity $nugetVerbosity
        $env:SignType = "Test"
    }

    if ($IBCMerge) {
        Write-Host "Installing MicroBuild IBCMerge plugin" -ForegroundColor $HeaderColor
        & "$toolsPath\Install-NuGetPackage.ps1" MicroBuild.Plugins.IBCMerge -source $MicroBuildPackageSource -Verbosity $nugetVerbosity
        $env:IBCMergeBranch = "master"
    }

    Write-Host "Successfully restored all dependencies" -ForegroundColor Yellow
}
catch {
    Write-Error "Aborting script due to error"
    exit $lastexitcode
}
finally {
    Pop-Location
}

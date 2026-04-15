<#
.SYNOPSIS
Downloads 32-bit and 64-bit procdump executables and returns the path to where they were installed.
#>
Join-Path (& "$PSScriptRoot\Download-NuGetPackage.ps1" -PackageId procdump -Version 0.0.1) 'bin'

<#
.SYNOPSIS
    Downloads the NuGet.exe tool and returns the path to it.
.PARAMETER NuGetVersion
    The version of the NuGet tool to acquire.
#>
Param(
    [Parameter()]
    [string]$NuGetVersion='7.3.1'
)

function Test-NuGetExecutableSignature {
    Param(
        [Parameter(Mandatory=$true)]
        [string]$Path
    )

    if (!(Test-Path $Path)) {
        return $false
    }

    $signature = Get-AuthenticodeSignature -FilePath $Path
    if ($signature.Status -eq [System.Management.Automation.SignatureStatus]::Valid -and
        $null -ne $signature.SignerCertificate -and
        $signature.SignerCertificate.Subject -like '*CN=Microsoft Corporation*') {
        Write-Verbose "NuGet executable signature is valid."
        return $true
    }

    Write-Verbose "NuGet executable signature is invalid."
    return $false
}

$toolsPath = & "$PSScriptRoot\Get-TempToolsPath.ps1"
$binaryToolsPath = Join-Path $toolsPath $NuGetVersion
if (!(Test-Path $binaryToolsPath)) { $null = mkdir $binaryToolsPath }
$nugetPath = Join-Path $binaryToolsPath nuget.exe

if (!(Test-Path $nugetPath) -or -not (Test-NuGetExecutableSignature -Path $nugetPath)) {
    Write-Host "Downloading nuget.exe $NuGetVersion..." -ForegroundColor Yellow
    (New-Object System.Net.WebClient).DownloadFile("https://dist.nuget.org/win-x86-commandline/v$NuGetVersion/NuGet.exe", $nugetPath)

    if (!(Test-NuGetExecutableSignature -Path $nugetPath)) {
        Remove-Item $nugetPath -Force -ErrorAction SilentlyContinue
        throw "Downloaded nuget.exe $NuGetVersion failed Authenticode signature validation."
    }
}

return (Resolve-Path $nugetPath).Path

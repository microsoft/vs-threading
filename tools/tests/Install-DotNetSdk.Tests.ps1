#!/usr/bin/env pwsh

$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot '..\Install-DotNetSdk.ps1'
$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile((Resolve-Path $scriptPath), [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) {
    throw $parseErrors[0]
}

foreach ($name in 'Test-DotNetRuntimeInstalled', 'Test-DotNetSdkInstalled') {
    $function = $ast.Find({
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
    }, $true)
    . ([scriptblock]::Create($function.Extent.Text))
}

$directory = Join-Path ([System.IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString())
try {
    foreach ($location in 'repo', 'user', 'machine') {
        foreach ($architecture in 'x64', 'x86', 'arm64') {
            $root = Join-Path $directory "$location\$architecture"
            foreach ($runtime in 'Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App', 'Microsoft.AspNetCore.App') {
                New-Item -ItemType Directory -Path (Join-Path $root "shared\$runtime\8.0.0") -Force | Out-Null
                $Upgrade = $false
                if (!(Test-DotNetRuntimeInstalled $root $runtime '8.0')) {
                    throw "Normal initialization must reuse the installed $runtime in $root."
                }

                if (Test-DotNetRuntimeInstalled $root $runtime '10.0') {
                    throw 'A different runtime channel must not satisfy the requested version.'
                }

                $Upgrade = $true
                if (Test-DotNetRuntimeInstalled $root $runtime '8.0') {
                    throw "Upgrade must check for newer patches of $runtime in $root."
                }
            }

            New-Item -ItemType Directory -Path (Join-Path $root 'sdk\10.0.100') -Force | Out-Null
            $Upgrade = $false
            if (!(Test-DotNetSdkInstalled $root $null '10.0')) {
                throw 'Normal initialization must reuse the installed SDK channel.'
            }

            $Upgrade = $true
            if (Test-DotNetSdkInstalled $root $null '10.0') {
                throw 'Upgrade must check for newer SDK channel patches.'
            }

            if (!(Test-DotNetSdkInstalled $root '10.0.100' $null)) {
                throw 'An exact SDK pin must still reuse the installed version during upgrades.'
            }
        }
    }

    $init = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\..\init.ps1') -Raw
    if ($init -notmatch 'Install-DotNetSdk\.ps1"[^\r\n]*-Upgrade:\$UpgradePrerequisites') {
        throw 'init.ps1 must forward UpgradePrerequisites to the SDK installer.'
    }

    Write-Host 'SDK installation detection and upgrade tests passed.'
}
finally {
    if (Test-Path -LiteralPath $directory) {
        Remove-Item -LiteralPath $directory -Recurse -Force
    }
}

#!/usr/bin/env pwsh

$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot '..\dotnet-test-cloud.ps1'
$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile((Resolve-Path $scriptPath), [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) {
    throw $parseErrors[0]
}

$diagnostics = $ast.Find({
    param($node)
    $node -is [System.Management.Automation.Language.IfStatementAst] -and
        $node.Extent.Text -match '^if \([^\r\n]+\) \{\s*\$nativeAotArgs \+= \$dumpSwitches\s*\}$'
}, $true)
if (!$diagnostics) {
    throw 'NativeAOT diagnostics conditional not found.'
}

$dumpSwitches = '--hangdump', '--hangdump-timeout', '5m', '--crashdump'
$oldOS = $env:OS
try {
    $env:OS = 'Windows_NT'
    $nativeAotArgs = @()
    . ([scriptblock]::Create($diagnostics.Extent.Text))
    if (($nativeAotArgs -join ',') -ne ($dumpSwitches -join ',')) {
        throw 'Windows NativeAOT tests must receive hang timeout and dump options.'
    }

    $env:OS = $null
    $nativeAotArgs = @()
    . ([scriptblock]::Create($diagnostics.Extent.Text))
    if ($nativeAotArgs.Count -ne 0) {
        throw 'Non-Windows NativeAOT tests must not receive Windows-only dump options.'
    }

    Write-Host 'NativeAOT diagnostics tests passed.'
}
finally {
    $env:OS = $oldOS
}

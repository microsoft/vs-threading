#!/usr/bin/env pwsh

$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot '..\dotnet-test-cloud.ps1'
$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile((Resolve-Path $scriptPath), [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) {
    throw $parseErrors[0]
}

$platformAssignments = @($ast.FindAll({
    param($node)
    $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
        $node.Left.Extent.Text -eq '$extraArgs' -and
        $node.Operator -eq 'Equals'
}, $true))
if ($platformAssignments.Count -ne 1) {
    throw 'Test arguments must preserve the NonWindows platform selection.'
}

$managedTest = $ast.Find({
    param($node)
    $node -is [System.Management.Automation.Language.CommandAst] -and
        $node.CommandElements.Count -gt 2 -and
        $node.CommandElements[0].Extent.Text -eq '$dotnet' -and
        $node.CommandElements[2].Extent.Text -eq '$solutionPath'
}, $true)
$managedArgs = @($managedTest.CommandElements | ForEach-Object { $_.Extent.Text })
$platformIndex = [Array]::IndexOf($managedArgs, '@extraArgs')
$separatorIndex = [Array]::IndexOf($managedArgs, '--')
if ($platformIndex -lt 0 -or $separatorIndex -lt 0 -or $platformIndex -gt $separatorIndex) {
    throw 'NonWindows platform selection must be passed to MSBuild, not the test runner.'
}

$nativeTest = $ast.Find({
    param($node)
    $node -is [System.Management.Automation.Language.CommandAst] -and
        $node.CommandElements.Count -gt 0 -and
        $node.CommandElements[0].Extent.Text -eq '$testExecutable'
}, $true)
if ($nativeTest.CommandElements.Extent.Text -contains '@extraArgs') {
    throw 'NativeAOT test executables cannot accept MSBuild platform arguments.'
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
$nativeOptions = $ast.Find({
    param($node)
    $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
        $node.Left.Extent.Text -eq '$nativeAotArgs'
}, $true)
$nativeAotTest = @{ ProjectName = 'Tests'; TargetFramework = 'net10.0' }
$testLogs = 'TestResults'
. ([scriptblock]::Create($nativeOptions.Extent.Text))
$nativeAotArgs = @($nativeAotArgs | ForEach-Object { [string]$_ })
$timeoutIndex = [Array]::IndexOf($nativeAotArgs, '--timeout')
if ($timeoutIndex -lt 0 -or $nativeAotArgs[$timeoutIndex + 1] -ne '5m') {
    throw 'NativeAOT tests must have a global timeout on every platform.'
}

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

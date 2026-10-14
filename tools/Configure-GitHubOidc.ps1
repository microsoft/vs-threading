#!/usr/bin/env pwsh

<#
.SYNOPSIS
    Configures immutable GitHub OIDC subjects and matching Entra federated credentials.
.DESCRIPTION
    Run from the repository to configure, after signing in with gh auth login and
    az login --tenant 72f988bf-86f1-41af-91ab-2d7cd011db47 --allow-no-subscriptions.
    Requires repository administration permission and permission to manage federated
    credentials on the Entra application. This changes the subject format for ALL
    OIDC workflows in the repository; migrate any other cloud trust policies first.
    Existing federated credentials are preserved. Feed permissions are not changed.
.PARAMETER ApplicationId
    The Entra application (client) ID to configure.
.PARAMETER TenantId
    The tenant in which the application is registered. The active az account must
    belong to this tenant.
.PARAMETER Branches
    Additional branches to trust. Pull requests and the repository's default
    branch are always included.
.EXAMPLE
    ./tools/Configure-GitHubOidc.ps1 -WhatIf
.EXAMPLE
    ./tools/Configure-GitHubOidc.ps1 -Branches main,microbuild,release
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
Param(
    [guid]$ApplicationId = '2799af29-63f3-404f-bdcf-67ff9c70abc9',
    [guid]$TenantId = '72f988bf-86f1-41af-91ab-2d7cd011db47',
    [ValidateNotNullOrEmpty()]
    [string[]]$Branches
)

$ErrorActionPreference = 'Stop'

function Invoke-CheckedCommand {
    Param(
        [string]$Command,
        [string[]]$Arguments
    )

    $output = & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Command $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }

    return $output
}

function Invoke-JsonCommand {
    Param(
        [string]$Command,
        [string[]]$Arguments,
        $Body
    )

    # JSON files avoid native-command quoting differences between Windows and Unix.
    $path = [System.IO.Path]::GetTempFileName()
    try {
        $Body | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $path -Encoding utf8
        Invoke-CheckedCommand $Command ($Arguments + $path)
    }
    finally {
        Remove-Item -LiteralPath $path -Force
    }
}

Get-Command gh, az -ErrorAction Stop | Out-Null
$repoName = (Invoke-CheckedCommand gh @('repo', 'view', '--json', 'nameWithOwner') | ConvertFrom-Json).nameWithOwner
if ([string]::IsNullOrWhiteSpace($repoName)) {
    throw 'gh did not identify a repository in the current directory.'
}

$repo = Invoke-CheckedCommand gh @('api', "repos/$repoName") | ConvertFrom-Json
if (!$repo.id -or !$repo.owner.id -or !$repo.owner.login -or !$repo.name -or !$repo.default_branch) {
    throw 'GitHub returned incomplete repository identity information.'
}

$oidcEndpoint = "repos/$($repo.full_name)/actions/oidc/customization/sub"
$currentSettings = Invoke-CheckedCommand gh @('api', $oidcEndpoint) | ConvertFrom-Json
$account = Invoke-CheckedCommand az @('account', 'show', '--output', 'json') | ConvertFrom-Json
if ($account.tenantId -ne $TenantId.ToString()) {
    throw "Sign in to tenant $TenantId with az login --tenant $TenantId --allow-no-subscriptions before running this script."
}

$issuer = 'https://token.actions.githubusercontent.com'
$audience = 'api://AzureADTokenExchange'
$prefix = "repo:$($repo.owner.login)@$($repo.owner.id)/$($repo.name)@$($repo.id)"
$trustedBranches = @($repo.default_branch)
if ($PSBoundParameters.ContainsKey('Branches')) {
    $trustedBranches += $Branches
}

$contexts = @('pull_request')
foreach ($branch in $trustedBranches) {
    if ([string]::IsNullOrWhiteSpace($branch)) {
        throw 'Branch names must not be empty or whitespace.'
    }

    $contexts += "ref:refs/heads/$($branch.Replace(':', '%3A'))"
}

$existing = @(Invoke-CheckedCommand az @('ad', 'app', 'federated-credential', 'list', '--id', $ApplicationId.ToString(), '--output', 'json') | ConvertFrom-Json)
$missing = @()
foreach ($context in ($contexts | Select-Object -Unique)) {
    $subject = "${prefix}:$context"
    $matching = @($existing | Where-Object { $_.issuer -ceq $issuer -and $_.subject -ceq $subject })
    if ($matching.Count -gt 0) {
        if (!($matching | Where-Object { @($_.audiences).Count -eq 1 -and $_.audiences[0] -ceq $audience })) {
            throw "An existing credential for '$subject' has an unexpected audience. Resolve it before continuing."
        }

        Write-Host "Reusing federated credential '$($matching[0].name)' for '$subject'."
        continue
    }

    $contextName = if ($context -eq 'pull_request') {
        'pull-request'
    } else {
        "branch-$($context.Substring('ref:refs/heads/'.Length).Replace('%3A', ':'))"
    }
    $readableName = "github-$($repo.owner.login)-$($repo.name)-$contextName"
    $name = $readableName -creplace '[^A-Za-z0-9._-]', '-'
    # Entra names must be URL-friendly and at most 120 characters. Preserve uniqueness
    # when replacing branch punctuation or shortening a long name.
    if ($name -cne $readableName -or $name.Length -gt 120) {
        $sha256 = [System.Security.Cryptography.SHA256]::Create()
        try {
            $hash = $sha256.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($subject))
            $suffix = ([System.BitConverter]::ToString($hash)).Replace('-', '').Substring(0, 16).ToLowerInvariant()
        }
        finally {
            $sha256.Dispose()
        }

        $name = "$($name.Substring(0, [Math]::Min($name.Length, 103)))-$suffix"
    }

    if (@($existing) + @($missing) | Where-Object { $_.name -eq $name }) {
        throw "Federated credential name '$name' is already used for a different trust. No existing credentials were modified."
    }

    $missing += @{
        name = $name
        issuer = $issuer
        subject = $subject
        audiences = @($audience)
        description = "GitHub Actions for $($repo.full_name): $context"
    }
}

$settingsMatch = $currentSettings.use_default -eq $true -and $currentSettings.use_immutable_subject -eq $true -and $currentSettings.sub_claim_prefix -ceq $prefix
if ($missing.Count -eq 0 -and $settingsMatch) {
    Write-Host "Immutable OIDC subjects and federated credentials are already configured for $($repo.full_name)."
    return
}

Write-Warning 'Changing the repository OIDC subject format affects every workflow, including existing environment and other cloud trust policies.'
if (!$PSCmdlet.ShouldProcess("$($repo.full_name) and Entra application $ApplicationId", "Ensure $($missing.Count) federated credentials and enable immutable default OIDC subjects")) {
    return
}

foreach ($credential in $missing) {
    Invoke-JsonCommand az @('ad', 'app', 'federated-credential', 'create', '--id', $ApplicationId.ToString(), '--output', 'none', '--parameters') $credential | Out-Null
    Write-Host "Created federated credential '$($credential.name)' for '$($credential.subject)'."
}

$credentials = @(Invoke-CheckedCommand az @('ad', 'app', 'federated-credential', 'list', '--id', $ApplicationId.ToString(), '--output', 'json') | ConvertFrom-Json)
foreach ($context in ($contexts | Select-Object -Unique)) {
    $subject = "${prefix}:$context"
    if (!($credentials | Where-Object { $_.issuer -ceq $issuer -and $_.subject -ceq $subject -and @($_.audiences).Count -eq 1 -and $_.audiences[0] -ceq $audience })) {
        throw "Federated credential verification failed for '$subject'."
    }
}

# Verify Azure trust before switching GitHub to the new subject format.
if (!$settingsMatch) {
    Invoke-JsonCommand gh @('api', '--method', 'PUT', $oidcEndpoint, '--input') @{
        use_default = $true
        use_immutable_subject = $true
    } | Out-Null
}

$settings = Invoke-CheckedCommand gh @('api', $oidcEndpoint) | ConvertFrom-Json
if ($settings.use_default -ne $true -or $settings.use_immutable_subject -ne $true -or $settings.sub_claim_prefix -cne $prefix) {
    throw 'GitHub OIDC settings did not match the requested immutable default subject format.'
}

Write-Host "Configured immutable OIDC subjects and federated credentials for $($repo.full_name)."

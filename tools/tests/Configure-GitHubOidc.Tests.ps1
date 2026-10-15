#!/usr/bin/env pwsh

# Run with pwsh -File tools/tests/Configure-GitHubOidc.Tests.ps1. No cloud changes are made.
$ErrorActionPreference = 'Stop'
$target = Join-Path $PSScriptRoot '..\Configure-GitHubOidc.ps1'
$tenant = '72f988bf-86f1-41af-91ab-2d7cd011db47'
$prefix = 'repo:ExampleOrg@123/ExampleRepo@456'

function Assert-True($Condition, [string]$Message) {
    if (!$Condition) {
        throw $Message
    }
}

function Reset-State {
    $script:state = @{
        Settings = @{ use_default = $true; use_immutable_subject = $false; sub_claim_prefix = 'repo:ExampleOrg/ExampleRepo' }
        Credentials = @()
        Writes = @()
        Tenant = $tenant
        Failure = ''
        BadVerification = $false
        MissingCreatedCredentials = $false
        Prefix = $prefix
    }
    $global:ConfigureGitHubOidcTestState = $script:state
}

function gh {
    $state = $global:ConfigureGitHubOidcTestState
    $global:LASTEXITCODE = 0
    if ($args[0] -eq 'repo') {
        return '{"nameWithOwner":"ExampleOrg/ExampleRepo"}'
    }

    if ($args[1] -eq 'repos/ExampleOrg/ExampleRepo') {
        return '{"id":456,"name":"ExampleRepo","full_name":"ExampleOrg/ExampleRepo","owner":{"id":123,"login":"ExampleOrg"},"default_branch":"trunk"}'
    }

    if ($args[1] -eq '--method') {
        $state.Writes += 'gh-put'
        if ($state.Failure -eq 'gh-put') {
            $global:LASTEXITCODE = 1
            return
        }

        $body = Get-Content -LiteralPath $args[-1] -Raw | ConvertFrom-Json
        Assert-True ($body.use_default -eq $true -and $body.use_immutable_subject -eq $true) 'Incorrect GitHub settings payload.'
        $state.Settings = @{ use_default = $true; use_immutable_subject = $true; sub_claim_prefix = $state.Prefix }
        return
    }

    Assert-True ($args[1] -eq 'repos/ExampleOrg/ExampleRepo/actions/oidc/customization/sub') 'Unexpected GitHub API endpoint.'
    if ($state.BadVerification -and $state.Writes -contains 'gh-put') {
        return '{"use_default":true,"use_immutable_subject":true,"sub_claim_prefix":"wrong"}'
    }

    return ($state.Settings | ConvertTo-Json)
}

function az {
    $state = $global:ConfigureGitHubOidcTestState
    $global:LASTEXITCODE = 0
    if ($args[0] -eq 'account') {
        return (@{ tenantId = $state.Tenant } | ConvertTo-Json)
    }

    Assert-True ($args[0] -eq 'ad' -and $args[1] -eq 'app' -and $args[2] -eq 'federated-credential') 'Unexpected Azure command.'
    Assert-True ($args[5] -eq '2799af29-63f3-404f-bdcf-67ff9c70abc9') 'Incorrect application ID.'
    if ($args[3] -eq 'list') {
        return (ConvertTo-Json -InputObject @($state.Credentials) -Depth 10)
    }

    Assert-True ($args[3] -eq 'create') 'Unexpected Azure mutation.'
    $state.Writes += 'az-create'
    if ($state.Failure -eq 'az-create') {
        $global:LASTEXITCODE = 1
        return
    }

    $body = Get-Content -LiteralPath $args[-1] -Raw | ConvertFrom-Json
    Assert-True ($body.issuer -ceq 'https://token.actions.githubusercontent.com') 'Incorrect issuer.'
    Assert-True ($body.audiences.Count -eq 1 -and $body.audiences[0] -ceq 'api://AzureADTokenExchange') 'Incorrect audience.'
    if (!$state.MissingCreatedCredentials) {
        $state.Credentials += $body
    }
}

function Assert-Fails([string]$Pattern, [hashtable]$Parameters = @{}) {
    $failed = $false
    try {
        & $target @Parameters -WarningAction SilentlyContinue
    }
    catch {
        Assert-True ($_.Exception.Message -like $Pattern) "Unexpected error: $($_.Exception.Message)"
        $failed = $true
    }

    Assert-True $failed 'Expected script to fail.'
}

Reset-State
& $target -WarningAction SilentlyContinue
$subjects = @($script:state.Credentials.subject)
Assert-True ($subjects.Count -eq 2) 'Expected two federated credentials.'
Assert-True ($subjects -cnotcontains "${prefix}:ref:refs/heads/microbuild") 'microbuild must not be trusted by default.'
foreach ($context in @('pull_request', 'ref:refs/heads/trunk')) {
    Assert-True ($subjects -ccontains "${prefix}:$context") "Missing subject: $context"
}
Assert-True (($script:state.Writes -join ',') -eq 'az-create,az-create,gh-put') 'Azure trust must be created before GitHub settings change.'
Assert-True (@($script:state.Credentials.name | Select-Object -Unique).Count -eq 2) 'Credential names must be unique.'
Assert-True ($script:state.Credentials.name -ccontains 'github-ExampleOrg-ExampleRepo-pull-request') 'PR credential name must be human-readable.'
Assert-True ($script:state.Credentials.name -ccontains 'github-ExampleOrg-ExampleRepo-branch-trunk') 'Branch credential name must be human-readable.'
$firstCredentialName = $script:state.Credentials[0].name

$script:state.Writes = @()
& $target -WarningAction SilentlyContinue
Assert-True ($script:state.Writes.Count -eq 0) 'Rerun must not duplicate credentials or rewrite settings.'

Reset-State
& $target -WhatIf -WarningAction SilentlyContinue
Assert-True ($script:state.Writes.Count -eq 0) 'WhatIf must not mutate either service.'

Reset-State
& $target -Branches @('release', 'release') -WarningAction SilentlyContinue
Assert-True ($script:state.Credentials.Count -eq 3) 'Duplicate branches must be deduplicated while retaining the default branch.'
Assert-True ($script:state.Credentials.subject -ccontains "${prefix}:ref:refs/heads/release") 'Custom branch subject missing.'
Assert-True ($script:state.Credentials.subject -ccontains "${prefix}:ref:refs/heads/trunk") 'Explicit branches must not replace the default branch.'

Reset-State
& $target -Branches microbuild -WarningAction SilentlyContinue
Assert-True ($script:state.Credentials.Count -eq 3) 'A single additional branch must produce PR, default branch, and additional branch credentials.'
Assert-True ($script:state.Credentials.subject -ccontains "${prefix}:ref:refs/heads/microbuild") 'Single-string branch argument must be trusted.'
Assert-True ($script:state.Credentials.subject -ccontains "${prefix}:ref:refs/heads/trunk") 'Default branch must be retained with a single-string argument.'

Reset-State
& $target -Branches trunk -WarningAction SilentlyContinue
Assert-True ($script:state.Credentials.Count -eq 2) 'Explicit default branch must not duplicate default trust.'

Reset-State
& $target -Branches @('feature/widget', 'feature-widget', ('long-' + ('x' * 150))) -WarningAction SilentlyContinue
$names = @($script:state.Credentials.name)
Assert-True (($names | Select-Object -Unique).Count -eq 5) 'Sanitized branch names must remain unique.'
foreach ($name in $names) {
    Assert-True ($name.Length -le 120 -and $name -cmatch '^[A-Za-z0-9._-]+$') 'Credential names must satisfy Entra limits.'
    Assert-True ($name.StartsWith('github-ExampleOrg-ExampleRepo-')) 'Sanitized names must retain repository names.'
}
Assert-True ($names -ccontains 'github-ExampleOrg-ExampleRepo-branch-feature-widget') 'URL-friendly branch names should not have a hash suffix.'
$script:state.Writes = @()
& $target -Branches @('feature/widget', 'feature-widget', ('long-' + ('x' * 150))) -WarningAction SilentlyContinue
Assert-True ($script:state.Writes.Count -eq 0) 'Sanitized names must remain idempotent.'

Reset-State
$script:state.Credentials = @(@{ name = 'unrelated'; issuer = 'other'; subject = 'other'; audiences = @('other') })
& $target -WarningAction SilentlyContinue
Assert-True ($script:state.Credentials[0].name -eq 'unrelated' -and $script:state.Credentials.Count -eq 3) 'Unrelated trust must be preserved.'

Reset-State
$script:state.Tenant = 'wrong-tenant'
Assert-Fails 'Sign in to tenant*'
Assert-True ($script:state.Writes.Count -eq 0) 'Wrong tenant must fail before mutations.'

Reset-State
$script:state.Failure = 'az-create'
Assert-Fails 'az *failed with exit code 1.'
Assert-True ($script:state.Writes -notcontains 'gh-put') 'Azure failure must prevent GitHub subject changes.'

Reset-State
$script:state.Failure = 'gh-put'
Assert-Fails 'gh *failed with exit code 1.'
$script:state.Failure = ''
$script:state.Writes = @()
& $target -WarningAction SilentlyContinue
Assert-True (($script:state.Writes -join ',') -eq 'gh-put') 'Retry after GitHub failure must reuse newly created Azure credentials.'

Reset-State
$script:state.Credentials = @(@{ name = 'wrong-audience'; issuer = 'https://token.actions.githubusercontent.com'; subject = "${prefix}:pull_request"; audiences = @('wrong') })
Assert-Fails '*unexpected audience*'
Assert-True ($script:state.Writes.Count -eq 0) 'Conflicting audience must fail before mutations.'

Reset-State
$script:state.Credentials = @(@{ name = $firstCredentialName; issuer = 'other'; subject = 'other'; audiences = @('other') })
Assert-Fails '*already used for a different trust*'
Assert-True ($script:state.Writes.Count -eq 0) 'Credential name collisions must fail before mutations.'

Reset-State
Assert-Fails 'Branch names must not be empty*' @{ Branches = @('   ') }
Assert-True ($script:state.Writes.Count -eq 0) 'Invalid branches must fail before mutations.'

Reset-State
$script:state.BadVerification = $true
Assert-Fails 'GitHub OIDC settings did not match*'

Reset-State
$script:state.MissingCreatedCredentials = $true
Assert-Fails 'Federated credential verification failed*'
Assert-True ($script:state.Writes -notcontains 'gh-put') 'Unverified Azure trust must prevent GitHub changes.'

Write-Host 'Configure-GitHubOidc tests passed.'
Remove-Variable ConfigureGitHubOidcTestState -Scope Global

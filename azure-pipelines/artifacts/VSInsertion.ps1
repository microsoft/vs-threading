# This artifact captures everything needed to insert into VS (NuGet packages, insertion metadata, etc.)

if ($IsMacOS -or $IsLinux) {
    # We only package up for insertions on Windows agents since they are where optprof can happen.
    Write-Verbose "Skipping VSInsertion artifact since we're not on Windows"
    return @{}
}

$RepoRoot = [System.IO.Path]::GetFullPath("$PSScriptRoot\..\..")
$config = 'Debug'
if ($env:BUILDCONFIGURATION) { $config = $env:BUILDCONFIGURATION }
$NuGetPackages = "$RepoRoot\bin\Packages\$config\NuGet"
$CoreXTPackages = "$RepoRoot\bin\Packages\$config\CoreXT"
if (-not (Test-Path $NuGetPackages)) { Write-Warning "No NuGet packages found. Has a build been run?"; return @{} }

$ArtifactBasePath = "$RepoRoot\obj\_artifacts"
$ArtifactPath = "$ArtifactBasePath\VSInsertion"
if (-not (Test-Path $ArtifactPath)) { New-Item -ItemType Directory -Path $ArtifactPath | Out-Null }

$profilingInputs = [xml](Get-Content -Path "$PSScriptRoot\..\ProfilingInputs.props")
$profilingInputs.Project.ItemGroup.TestStore.Include = "vstsdrop:" + (& "$PSScriptRoot\..\variables\ProfilingInputsDropName.ps1")
$profilingInputs.Save("$ArtifactPath\ProfilingInputs.props")

$nbgv = & "$PSScriptRoot\..\Get-nbgv.ps1"
$InsertionMetadataVersion = $(& $nbgv get-version -p "$RepoRoot\src" -f json | ConvertFrom-Json).NuGetPackageVersion
if ($env:BUILD_BUILDID) {
    # We must ensure unique versions for the insertion metadata package so
    # it can contain information that is unique to this build.
    # In particular it includes the ProfilingInputsDropName, which contains the BuildId.
    # A non-unique package version here may collide with a prior run of this same commit,
    # ultimately resulting in a failure of the optprof run.
    $InsertionMetadataVersion += '.' + $env:BUILD_BUILDID
}
& (& "$PSScriptRoot\..\Get-NuGetTool.ps1") pack "$PSScriptRoot\..\InsertionMetadataPackage.nuspec" -OutputDirectory $CoreXTPackages -BasePath $ArtifactPath -Version $InsertionMetadataVersion | Out-Null
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

# This artifact is not ready if we're running on the devdiv AzDO account and we don't have an SBOM yet.
if ($env:SYSTEM_COLLECTIONID -eq '011b8bdf-6d56-4f87-be0d-0092136884d9' -and -not (Test-Path $NuGetPackages/_manifest)) { return @{} }

@{
    "$NuGetPackages" = (Get-ChildItem -Recurse $NuGetPackages);
    "$CoreXTPackages" = (Get-ChildItem "$CoreXTPackages\Microsoft.VisualStudio.Threading.VSInsertionMetadata.$InsertionMetadataVersion.nupkg");
}

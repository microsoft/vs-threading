# This artifact captures everything needed to insert into VS (NuGet packages, insertion metadata, etc.)

[CmdletBinding()]
Param (
)

if ($IsMacOS -or $IsLinux) {
    # We only package up for insertions on Windows agents since they are where optprof can happen.
    Write-Verbose "Skipping VSInsertion artifact since we're not on Windows."
    return @{}
}

$RepoRoot = [System.IO.Path]::GetFullPath("$PSScriptRoot\..\..")
$BuildConfiguration = $env:BUILDCONFIGURATION
if (!$BuildConfiguration) {
    $BuildConfiguration = 'Debug'
}

$PackagesRoot = "$RepoRoot/bin/Packages/$BuildConfiguration/NuGet"

if (!(Test-Path $PackagesRoot)) {
    Write-Warning "Skipping because packages haven't been built yet."
    return @{}
}

$result = @{
    "$PackagesRoot" = (Get-ChildItem $PackagesRoot -Recurse)
}

if ($env:IsOptProf) {
    $CoreXTPackages = "$RepoRoot/bin/Packages/$BuildConfiguration/CoreXT"

    $ArtifactBasePath = "$RepoRoot\obj\_artifacts"
    $ArtifactPath = "$ArtifactBasePath\VSInsertion"
    if (-not (Test-Path $ArtifactPath)) { New-Item -ItemType Directory -Path $ArtifactPath | Out-Null }

    $profilingInputs = [xml](Get-Content -Path "$PSScriptRoot\..\ProfilingInputs.props")
    $profilingInputs.Project.ItemGroup.TestStore.Include = "vstsdrop:" + (& "$PSScriptRoot\..\variables\ProfilingInputsDropName.ps1")
    $profilingInputs.Save("$ArtifactPath\ProfilingInputs.props")

    $InsertionMetadataVersion = $(dotnet tool run nbgv get-version -p "$RepoRoot\src" -f json | ConvertFrom-Json).NuGetPackageVersion
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

    $result["$CoreXTPackages"] = (Get-ChildItem "$CoreXTPackages\LibraryName.VSInsertionMetadata.$InsertionMetadataVersion.nupkg");
}

$result

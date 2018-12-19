# This script translates all the artifacts described by _all.ps1
# into commands that instruct VSTS to actually collect those artifacts.

$RepoRoot = [System.IO.Path]::GetFullPath("$PSScriptRoot\..\..")
if (!$env:BuildConfiguration) { throw "BuildConfiguration environment variable must be set." }
if ($env:Build_ArtifactStagingDirectory) {
    $ArtifactStagingFolder = $env:Build_ArtifactStagingDirectory
} else {
    $ArtifactStagingFolder = "$RepoRoot\obj\_artifacts"
    if (Test-Path $ArtifactStagingFolder) {
        Remove-Item $ArtifactStagingFolder -Recurse -Force
    }
}

function mklink {
    cmd /c mklink $args
}

# Stage all artifacts
$Artifacts = & "$PSScriptRoot\_all.ps1"
$Artifacts |% {
    $DestinationFolder = (Join-Path (Join-Path $ArtifactStagingFolder $_.ArtifactName) $_.ContainerFolder).TrimEnd('\')
    $Name = Split-Path $_.Source -Leaf

    #Write-Host "$($_.Source) -> $($_.ArtifactName)\$($_.ContainerFolder)" -ForegroundColor Yellow

    if (-not (Test-Path $DestinationFolder)) { New-Item -ItemType Directory -Path $DestinationFolder | Out-Null }
    if (Test-Path -PathType Leaf $_.Source) { # skip folders
        mklink "$DestinationFolder\$Name" $_.Source
    }
}

$Artifacts |% { $_.ArtifactName } | Get-Unique |% {
    Write-Host "##vso[artifact.upload containerfolder=$_;artifactname=$_;]$ArtifactStagingFolder\$_"
}

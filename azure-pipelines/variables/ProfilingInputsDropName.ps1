if ($env:System_TeamProject) {
    "ProfilingInputs/$env:System_TeamProject/$env:Build_Repository_Name/$env:Build_SourceBranchName/$env:Build_BuildId"
} else {
    Write-Warning "No Azure Pipelines build detected. No VSTS drop name will be computed."
}

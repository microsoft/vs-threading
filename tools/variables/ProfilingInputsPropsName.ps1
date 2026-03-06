if ($env:SYSTEM_TEAMPROJECT) {
    $repoName = $env:BUILD_REPOSITORY_NAME.Replace('/', '.')
    "$env:SYSTEM_TEAMPROJECT.$repoName.props"
} else {
    Write-Warning "No Azure Pipelines build detected. No profiling inputs filename will be computed."
}

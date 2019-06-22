if ($env:ComputerName.StartsWith('factoryvm', [StringComparison]::OrdinalIgnoreCase)) {
    Write-Host "Running on hosted queue"
    Write-Output $true
} else {
    Write-Output $false
}

if ($env:SYSTEM_COLLECTIONID -eq '011b8bdf-6d56-4f87-be0d-0092136884d9') {
    Write-Host "Running on official devdiv account: $env:System_TeamFoundationCollectionUri"
} elseif ($env:SYSTEM_COLLECTIONID) {
    Write-Host "Running under OSS account: $env:System_TeamFoundationCollectionUri"
}

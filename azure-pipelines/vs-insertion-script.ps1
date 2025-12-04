# List of build artifact files [Source => Destination] to be committed into the VS repo.
$FilesToCommit = @{
    "$env:PROFILINGINPUTSPROPSNAME" = "src/Tests/config/runsettings/Official/OptProf/External/$env:PROFILINGINPUTSPROPSNAME";
}

foreach ($File in $FilesToCommit.GetEnumerator()) {
    $SourcePath = Join-Path $PSScriptRoot $File.Key
    if (Test-Path $SourcePath) {
        $DestinationPath = Join-Path (Get-Location) $File.Value
        Write-Host "Copying $SourcePath to $DestinationPath"
        Copy-Item -Path $SourcePath -Destination $DestinationPath
        git add $DestinationPath
    }
    else {
        Write-Host "$SourcePath is not present, skipping"
    }
}

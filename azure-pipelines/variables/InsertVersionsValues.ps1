$MacroName = 'MicrosoftVisualStudioThreadingVersion'
$SampleProject = "$PSScriptRoot\..\..\src\Microsoft.VisualStudio.Threading"
try {
    $json = dotnet tool run nbgv -- get-version --project $SampleProject --format json
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Exit code is $LASTEXITCODE. STDOUT follows"
        Write-Error $json
        throw
    }
    [string]::join(',',(@{
        ($MacroName) = & { ($json | ConvertFrom-Json).AssemblyVersion };
    }.GetEnumerator() |% { "$($_.key)=$($_.value)" }))
}
catch {
    Write-Error $_
    Write-Error "Failed. Diagnostic data proceeds:"
    [string]::join(',',(@{
        ($MacroName) = & {
            Write-Host "dotnet tool run nbgv get-version --project `"$SampleProject`" --format json"
            $result1 = dotnet tool run nbgv get-version --project $SampleProject --format json
            Write-Host $result1
            $result2 = dotnet tool run nbgv get-version --project $SampleProject --format json | ConvertFrom-Json
            Write-Host $result2
            Write-Host $result2.AssemblyVersion
            Write-Host (dotnet tool run nbgv get-version --project $SampleProject --format json | ConvertFrom-Json).AssemblyVersion
            Write-Output (dotnet tool run nbgv get-version --project $SampleProject --format json | ConvertFrom-Json).AssemblyVersion
        };
    }.GetEnumerator() |% { "$($_.key)=$($_.value)" }))

    Write-Host "one more..."
    $r = & { (dotnet tool run nbgv get-version --project $SampleProject --format json | ConvertFrom-Json).AssemblyVersion };
    Write-Host $r
}

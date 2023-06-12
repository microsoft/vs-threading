$MacroName = 'MicrosoftVisualStudioThreadingVersion'
$SampleProject = "$PSScriptRoot\..\..\src\Microsoft.VisualStudio.Threading"
[string]::join(',',(@{
    ($MacroName) = & { (dotnet tool run nbgv -- get-version --project $SampleProject --format json | ConvertFrom-Json).AssemblyVersion };
}.GetEnumerator() |% { "$($_.key)=$($_.value)" }))

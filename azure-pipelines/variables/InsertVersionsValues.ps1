[string]::join(',',(@{
    'MicrosoftVisualStudioThreadingVersion' = & { (nbgv get-version --project "$PSScriptRoot\..\..\src\Microsoft.VisualStudio.Threading" --format json | ConvertFrom-Json).AssemblyVersion };
}.GetEnumerator() |% { "$($_.key)=$($_.value)" }))

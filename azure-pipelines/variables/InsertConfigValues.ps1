$config = $env:BUILDCONFIGURATION
if (!$config) { $config = 'Debug' }
$BinPath = [System.IO.Path]::GetFullPath("$PSScriptRoot\..\..\bin\Packages\$config")

$dirsToSearch = "$BinPath\NuGet\*.nupkg" |? { Test-Path $_ }
$icv=@()

if ($dirsToSearch) {
    Get-ChildItem -Path $dirsToSearch |% {
        if ($_.Name -match "^(.*?)\.(\d+\.\d+\.\d+(?:\.\d+)?(?:-.*?)?)(?:\.symbols)?\.nupkg$") {
            $id = $Matches[1]
            $version = $Matches[2]
            $icv += "$id=$version"
        }
    }
}

Write-Output ([string]::join(',',$icv))

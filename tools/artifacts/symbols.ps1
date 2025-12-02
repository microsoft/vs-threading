$BinPath = [System.IO.Path]::GetFullPath("$PSScriptRoot/../../bin")
$ExternalPath = [System.IO.Path]::GetFullPath("$PSScriptRoot/../../obj/SymbolsPackages")
if (!(Test-Path $BinPath)) { return }
$symbolfiles = & "$PSScriptRoot/../Get-SymbolFiles.ps1" -Path $BinPath | Get-Unique
$ExternalFiles = & "$PSScriptRoot/../Get-ExternalSymbolFiles.ps1"

@{
    "$BinPath" = $SymbolFiles;
    "$ExternalPath" = $ExternalFiles;
}

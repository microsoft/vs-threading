param($installPath, $toolsPath, $package, $project)

$p = Get-Project

$analyzerFilePath = join-path $toolsPath "Microsoft.VisualStudio.Threading.Analyzers.dll"

$p.Object.AnalyzerReferences.Remove("$analyzerFilePath")
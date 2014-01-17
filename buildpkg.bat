@ECHO OFF

if "%1"=="" (
    ECHO USAGE: %0 version
    EXIT /b 1
)

msbuild "%~dp0Microsoft.VisualStudio.Threading\Microsoft.VisualStudioThreading.csproj" /p:Configuration=Release /v:minimal /nologo

@echo on
nuget pack "%~dp0Microsoft.Threading.nuspec" -symbols -OutputDirectory Microsoft.VisualStudio.Threading\bin -Version %1
echo Package built: "%~dp0Microsoft.VisualStudio.Threading\bin\Microsoft.Threading.%1.nupkg"
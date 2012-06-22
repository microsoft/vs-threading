@ECHO OFF

if "%1"=="" (
    ECHO USAGE: %0 version
    EXIT /b 1
)

msbuild "%~dp0Microsoft.Threading\Microsoft.Threading.csproj" /p:Configuration=Release	/v:minimal /nologo

@echo on
nuget pack "%~dp0Microsoft.Threading.nuspec" -symbols -OutputDirectory Microsoft.Threading\bin -Version %1

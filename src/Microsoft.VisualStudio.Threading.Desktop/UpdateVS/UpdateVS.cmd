@if "%_echo%"=="" echo off

CALL "%~dp0RunMeElevated.cmd" %0 %*
IF %ERRORLEVEL%==200 EXIT /B

:: Set up environment for this script.
setlocal
set SysProgramFiles=%ProgramFiles%
if NOT "%ProgramFiles(x86)%"=="" (
  set "SysProgramFiles=%ProgramFiles(x86)%"
)
set Common7=%SysProgramFiles%\Microsoft Visual Studio 14.0\Common7
set Common7Tools=%Common7%\Tools
set CommonIDE=%Common7%\IDE
set PrivateAssemblies=%CommonIDE%\PrivateAssemblies
set PublicAssemblies=%CommonIDE%\PublicAssemblies
if /I "%ROBOCOPY%"=="" SET ROBOCOPY=robocopy /NJH /NJS /NDL /XX /W:1

@ECHO Installing %AssemblyToInstall% into the GAC
SET AssemblyToInstall=Microsoft.VisualStudio.Threading
SET GACVSVersion=14.0
REM gacutil isn't available on Win8Express drops, and seems to randomly fail anyway.
REM So instead, we use robocopy to get both the .dll and its accompanying .pdb in place.
%robocopy% "%~dp0.." "%WINDIR%\Microsoft.NET\assembly\GAC_MSIL\%AssemblyToInstall%\v4.0_%GACVSVersion%.0.0__b03f5f7f11d50a3a" %AssemblyToInstall%.pdb %AssemblyToInstall%.dll

@ECHO Invalidating MEF cache...
for %%i in (devenv VSWinExpress) do (
    IF EXIST "%COMMONIDE%\%%i.exe" "%COMMONIDE%\%%i.exe" /updateConfiguration
)

exit /b 0

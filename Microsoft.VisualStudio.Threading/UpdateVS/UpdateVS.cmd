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

@ECHO Copying files into Program Files locations
%ROBOCOPY% "%~dp0.." "%PublicAssemblies%" Microsoft.VisualStudio.Threading.???

@ECHO Invalidating MEF cache...
for %%i in (devenv VSWinExpress) do (
    IF EXIST "%COMMONIDE%\%%i.exe" "%COMMONIDE%\%%i.exe" /updateConfiguration
)

exit /b 0

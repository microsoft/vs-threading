@echo off
CALL "%~dp0Packages\MicroBuild.VisualStudio.1.0.70-rc-gaf0d7eb797\tools\RunMeElevated.cmd" %0 %*
IF %ERRORLEVEL%==200 EXIT /B

setlocal

ECHO This script will remove MS.VS.Threading from the GAC
ECHO and ensure it is still found within the VS probing path.
PAUSE

IF "%ProgramFiles(x86)%"=="" SET PF32=%PROGRAMFILES%
IF "%PF32%"=="" SET PF32=%ProgramFiles(x86)%

SET Common7IDE=%PF32%\Microsoft Visual Studio 14.0\Common7\IDE
SET GAC=%windir%\Microsoft.NET\assembly\GAC_MSIL\

:: Ungac
IF EXIST "%GAC%Microsoft.VisualStudio.Threading\v4.0_14.0.0.0__b03f5f7f11d50a3a" (
    ROBOCOPY /W:1 /NJH /NJS /NDL "%GAC%Microsoft.VisualStudio.Threading\v4.0_14.0.0.0__b03f5f7f11d50a3a" "%Common7IDE%\PublicAssemblies" Microsoft.VisualStudio.Threading.dll
    rd /s /q "%GAC%Microsoft.VisualStudio.Threading\v4.0_14.0.0.0__b03f5f7f11d50a3a"
)

:: Also remove entries from the registry that cause these assemblies to be automatically re-GAC'd
call :RemoveAutoGacRegValue Microsoft.VisualStudio.Threading

exit /b 0

:RemoveAutoGacRegValue

    setlocal enableDelayedExpansion
    SET KEY=HKEY_CLASSES_ROOT\Installer\Assemblies\Global

    for /F "tokens=1,*" %%v in ('reg query "%KEY%" /f %1 ^| findstr /i /c:"%1" ^| findstr /c:"14.0.0.0"') do (
        SET val=%%v
        REG DELETE "%KEY%" /f /v "!val:"=\"!"
    )

    endlocal 

@echo off
setlocal

SET SRC=D:\VSPro_Platform\src\Localize
SET DST=%~dp0loc\lcl
SET relativePath=vsproject\platform\Microsoft.VisualStudio.Threading.dll.lcl
for /d %%i in (%SRC%\*) do (
	IF EXIST %%i\%RELATIVEPATH% (
		IF NOT EXIST "%DST%\%%~ni" MD "%DST%\%%~ni""
		COPY "%%i\%RELATIVEPATH%" "%DST%\%%~nxi""
	)
)

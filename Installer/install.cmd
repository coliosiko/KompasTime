@echo off
if not exist "%~dp0setup.ps1" (
    echo Extract the entire KompasTime ZIP with Extract All, then run this file from the extracted folder.
    pause
    exit /b 2
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup.ps1" %*
set "RESULT=%ERRORLEVEL%"
pause
exit /b %RESULT%

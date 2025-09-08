@echo off
setlocal

rem Simple launcher for hot reload via PowerShell script
rem Ensures working directory is the script directory

set "PS=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if not exist "%PS%" set "PS=powershell"

pushd "%~dp0"
echo [dev.bat] Running from: %CD%
"%PS%" -NoProfile -ExecutionPolicy Bypass -File "%CD%\dev.ps1" %*
set "ERR=%ERRORLEVEL%"
popd
endlocal & exit /b %ERR%

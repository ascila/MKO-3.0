@echo off
setlocal

rem Simple launcher for hot reload via PowerShell script
rem Pass-through to PowerShell script. For snapshots, use:
rem   dev.bat snap "descripcion"

set "PS=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if not exist "%PS%" set "PS=powershell"

rem Snapshot command handled by dev.ps1 (Task/Desc)

pushd "%~dp0"
echo [dev.bat] Running from: %CD%
"%PS%" -NoProfile -ExecutionPolicy Bypass -File "%CD%\dev.ps1" %*
set "ERR=%ERRORLEVEL%"
popd
endlocal & exit /b %ERR%

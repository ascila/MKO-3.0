@echo off
setlocal

rem Simple launcher for hot reload via PowerShell script
rem Also supports snapshots: dev.bat snap "descripcion"

set "PS=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if not exist "%PS%" set "PS=powershell"

rem Handle snapshot command shortcut
if /I "%~1"=="snap" (
  shift
  set "DESC=%*"
  pushd "%~dp0"
  echo [dev.bat] Snapshot: %DESC%
  "%PS%" -NoProfile -ExecutionPolicy Bypass -File "%CD%\tools\snapshot.ps1" -Desc "%DESC%"
  set "ERR=%ERRORLEVEL%"
  popd
  endlocal & exit /b %ERR%
)

pushd "%~dp0"
echo [dev.bat] Running from: %CD%
"%PS%" -NoProfile -ExecutionPolicy Bypass -File "%CD%\dev.ps1" %*
set "ERR=%ERRORLEVEL%"
popd
endlocal & exit /b %ERR%

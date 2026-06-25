@echo off
setlocal

REM Build launcher - resolves paths relative to this script so it works from any location.
set "SCRIPT_DIR=%~dp0"

cd /d "%SCRIPT_DIR%"

powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%AppWrapper\F76ManagerApp\build.ps1" %*

pause

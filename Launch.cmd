@echo off
setlocal
cd /d "%~dp0"
if not exist "dist\CodexUsageWidget.exe" (
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Build.ps1"
    if errorlevel 1 (pause & exit /b 1)
)
start "" "%~dp0dist\CodexUsageWidget.exe"

@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Build.ps1"
if errorlevel 1 (pause & exit /b 1)
echo Built. Double-click Launch.cmd to open the widget.
pause

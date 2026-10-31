@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Start-Lens.ps1"
if errorlevel 1 pause

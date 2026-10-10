@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0winui-editor\run-editor.ps1"
if errorlevel 1 pause

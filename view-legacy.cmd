@echo off
setlocal
cd /d "%~dp0legacy\python"
python app.py
if errorlevel 1 pause

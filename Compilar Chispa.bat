@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Iniciar Chispa.ps1" -Recompilar -SoloCompilar
if errorlevel 1 pause

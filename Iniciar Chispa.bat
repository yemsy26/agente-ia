@echo off
rem Comprueba si el codigo, la guia o las piezas cambiaron antes de iniciar.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Iniciar Chispa.ps1"
if errorlevel 1 pause

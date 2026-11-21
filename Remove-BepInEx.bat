@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Remove-BepInEx.ps1" %*
pause

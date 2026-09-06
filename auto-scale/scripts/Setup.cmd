@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Setup.ps1" %*
exit /b %ERRORLEVEL%

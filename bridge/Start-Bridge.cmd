@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Start-Bridge.ps1" %*
if errorlevel 1 (
    echo.
    echo DLSS NR Bridge launcher failed.
    pause
)

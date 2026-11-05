@echo off
setlocal
cd /d "%~dp0"

:: Check for Administrative privileges
NET SESSION >nul 2>&1
IF %ERRORLEVEL% NEQ 0 (
    echo Requesting Administrator privileges...
    powershell -NoProfile -Command "Start-Process cmd -ArgumentList '/c \"\"%~f0\"\"' -Verb RunAs"
    exit /b
)

title Antigravity PC Optimizer - Elevated Execution
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Optimize-PC-Admin.ps1"
echo.
pause

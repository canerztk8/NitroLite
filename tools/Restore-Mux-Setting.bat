@echo off
setlocal
cd /d "%~dp0"

:: Check for Administrative privileges
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo Yonetici yetkisi isteniyor...
    powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

title Acer NitroSense Servis Guncelleme ve Onarma
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Restore-Mux-Setting.ps1"
echo.
pause

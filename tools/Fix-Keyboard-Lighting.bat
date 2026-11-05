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

title Klavye Aydinlatma Servisi Onarimi (AcerLightingService)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Fix-Keyboard-Lighting.ps1"
echo.
pause

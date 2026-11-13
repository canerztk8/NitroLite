@echo off
setlocal
cd /d "%~dp0"

echo ========================================================
echo   NitroSense MUX Switch Secenegi (NitroLite Entegre)
echo ========================================================
echo.
echo NOT: Bu arac artik tek bir yerde, dogrudan NitroLite
echo arayuzu icerisindeki "Sistem Servisleri ve Araclar"
echo sekmesine entegre edilmistir.
echo.

set "EXE_PATH="
if exist "%~dp0..\artifacts\NitroLite\NitroLite.exe" set "EXE_PATH=%~dp0..\artifacts\NitroLite\NitroLite.exe"
if exist "%~dp0..\NitroLite\bin\Release\net10.0-windows\NitroLite.exe" set "EXE_PATH=%~dp0..\NitroLite\bin\Release\net10.0-windows\NitroLite.exe"
if exist "%~dp0..\NitroLite.exe" set "EXE_PATH=%~dp0..\NitroLite.exe"

if defined EXE_PATH (
    echo NitroLite ana calistirilabilir dosyasi bulundu: %EXE_PATH%
    echo Islem NitroLite uzerinden yurutuluyor...
    "%EXE_PATH%" --restore-mux --yes
    if %errorlevel% equ 0 (
        echo [BASARILI] MUX Switch kaydi basariyla aktif edildi.
    ) else (
        echo [UYARI] Islem sirasinda bir sorun olustu veya yonetici izni verilmedi.
    )
    echo.
    pause
    exit /b
)

:: Fallback if NitroLite.exe not compiled yet
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo Yonetici yetkisi isteniyor...
    powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Restore-Mux-Setting.ps1"
echo.
pause

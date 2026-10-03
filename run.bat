@echo off

set EXE_PATH=%~dp0bin\Release\net8.0-windows\win-x64\publish\SPWare.WindSound.exe

if not exist "%EXE_PATH%" (
    echo [ERROR] The built program was not found: %EXE_PATH%
    echo Run build.bat first.
    pause
    exit /b 1
)

start "" "%EXE_PATH%"
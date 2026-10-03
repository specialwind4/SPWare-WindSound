@echo off

set EXE_PATH=%~dp0SPWare.VirtualSoundCanvas\bin\Release\net8.0-windows\win-x64\publish\SPWare.WindSound.exe

if not exist "%EXE_PATH%" (
    echo [오류] 빌드된 실행 파일을 찾을 수 없습니다: %EXE_PATH%
    echo 먼저 build.bat 을 실행해서 빌드해주세요.
    pause
    exit /b 1
)

start "" "%EXE_PATH%"

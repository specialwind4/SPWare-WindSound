@echo off
setlocal

echo ================================================
echo  SPWare Virtual Sound Canvas - 빌드 스크립트
echo ================================================
echo.

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [오류] dotnet 명령을 찾을 수 없습니다.
    echo .NET 8 SDK를 먼저 설치해주세요: https://dotnet.microsoft.com/download/dotnet/8.0
    echo.
    pause
    exit /b 1
)

echo [1/2] dotnet SDK 버전 확인...
dotnet --version
echo.

echo [2/2] Release 빌드 및 단일 exe 배포 중... (인터넷 연결 필요, 첫 실행은 시간이 걸립니다)
dotnet publish "%~dp0SPWare.VirtualSoundCanvas.csproj" -c Release -r win-x64

if errorlevel 1 (
    echo.
    echo [오류] 빌드에 실패했습니다. 위 로그를 확인해주세요.
    pause
    exit /b 1
)

set OUT_DIR=%~dp0bin\Release\net8.0-windows\win-x64\publish

echo.
echo ================================================
echo  빌드 완료!
echo  결과물: %OUT_DIR%\SPWare.WindSound.exe
echo ================================================
echo.

choice /M "결과 폴더를 지금 열까요?"
if errorlevel 2 goto :ask_shortcut
explorer "%OUT_DIR%"

:ask_shortcut
choice /M "바탕화면에 바로가기를 만들까요?"
if errorlevel 2 goto :end
call "%~dp0create_desktop_shortcut.bat"

:end
pause

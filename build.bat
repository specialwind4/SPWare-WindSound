@echo off
setlocal

echo ================================================
echo  SPWare Wind Sound - build script
echo ================================================
echo.

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [ERROR] The dotnet command was not found.
    echo Install the .NET 8 SDK first: https://dotnet.microsoft.com/download/dotnet/8.0
    echo.
    pause
    exit /b 1
)

echo [1/2] dotnet SDK version...
dotnet --version
echo.

echo [2/2] Release build and single-exe publish... (needs internet; the first run takes a while)
dotnet publish "%~dp0SPWare.VirtualSoundCanvas.csproj" -c Release -r win-x64

if errorlevel 1 (
    echo.
    echo [ERROR] The build failed. Check the log above.
    pause
    exit /b 1
)

set OUT_DIR=%~dp0bin\Release\net8.0-windows\win-x64\publish

echo.
echo ================================================
echo  Build finished!
echo  Output: %OUT_DIR%\SPWare.WindSound.exe
echo ================================================
echo.

choice /M "Open the output folder now"
if errorlevel 2 goto :ask_shortcut
explorer "%OUT_DIR%"

:ask_shortcut
choice /M "Create a desktop shortcut"
if errorlevel 2 goto :end
call "%~dp0create_desktop_shortcut.bat"

:end
pause
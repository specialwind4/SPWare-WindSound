@echo off
chcp 65001 >nul
setlocal
rem =====================================================================
rem  SPWare Wind Sound - create a desktop shortcut (icon)
rem
rem  NOTE: this file is saved as UTF-8 WITHOUT BOM, with CRLF line endings.
rem  The "chcp 65001" on line 2 lets the Korean messages below display
rem  correctly. Keep every comment in this file ASCII-only: after chcp,
rem  cmd.exe can mis-read non-ASCII comment lines as commands. Korean text
rem  is used only in the echo messages (that is safe).
rem
rem  Put this file next to SPWare.WindSound.exe and run it. It also works
rem  after the whole publish folder is copied to another PC.
rem
rem  Usage:  create_desktop_shortcut.bat              (shortcut on the desktop)
rem          create_desktop_shortcut.bat "C:\some\dir"  (shortcut in that folder)
rem =====================================================================

rem --- find the exe: same folder first, then the development publish folder ---
set "SC_EXE=%~dp0SPWare.WindSound.exe"
if not exist "%SC_EXE%" set "SC_EXE=%~dp0bin\Release\net8.0-windows\win-x64\publish\SPWare.WindSound.exe"
if not exist "%SC_EXE%" set "SC_EXE=%~dp0SPWare.VirtualSoundCanvas\bin\Release\net8.0-windows\win-x64\publish\SPWare.WindSound.exe"

if not exist "%SC_EXE%" (
    echo [오류] SPWare.WindSound.exe 를 찾을 수 없습니다.
    echo 이 파일을 SPWare.WindSound.exe 와 같은 폴더에 두고 다시 실행하세요.
    if "%~1"=="" pause
    exit /b 1
)

rem --- target folder for the shortcut (empty = PowerShell finds the desktop) ---
set "SC_TARGET=%~1"

echo 바로가기를 만드는 중...

rem Paths go to PowerShell through environment variables, not the command line,
rem so Korean folder names cannot be mangled by a code page conversion.
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ws = New-Object -ComObject WScript.Shell; " ^
  "$dir = $env:SC_TARGET; " ^
  "if ([string]::IsNullOrEmpty($dir)) { $dir = [Environment]::GetFolderPath('Desktop') }; " ^
  "$lnk = $ws.CreateShortcut((Join-Path $dir 'SPWare Wind Sound.lnk')); " ^
  "$lnk.TargetPath = $env:SC_EXE; " ^
  "$lnk.WorkingDirectory = (Split-Path $env:SC_EXE); " ^
  "$lnk.IconLocation = ($env:SC_EXE + ',0'); " ^
  "$lnk.Description = 'SPWare Wind Sound'; " ^
  "$lnk.Save(); " ^
  "Write-Output ('만든 바로가기: ' + $lnk.FullName)"

if errorlevel 1 (
    echo [오류] 바로가기를 만들지 못했습니다.
    if "%~1"=="" pause
    exit /b 1
)

echo 완료되었습니다.
if "%~1"=="" pause

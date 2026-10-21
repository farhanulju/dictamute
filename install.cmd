@echo off
rem Builds DictaMute from source, installs it to %LOCALAPPDATA%\DictaMute, turns on
rem "Start with Windows", and starts it. (Or just download DictaMute.exe from Releases.)
setlocal
cd /d "%~dp0"
call build.cmd || exit /b 1

set DEST=%LOCALAPPDATA%\DictaMute
taskkill /im DictaMute.exe /f >nul 2>&1
timeout /t 1 /nobreak >nul
if not exist "%DEST%" mkdir "%DEST%"
copy /y bin\DictaMute.exe "%DEST%\" >nul || exit /b 1
copy /y bin\CaptureProbe.exe "%DEST%\" >nul || exit /b 1

reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v DictaMute /t REG_SZ /d "\"%DEST%\DictaMute.exe\"" /f >nul || exit /b 1

start "" "%DEST%\DictaMute.exe"
echo Installed to %DEST% with Start with Windows on. Look for the shield icon in the system tray.

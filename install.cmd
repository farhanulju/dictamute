@echo off
rem Builds TeamsMicHold, installs it to %LOCALAPPDATA%\TeamsMicHold, starts it,
rem and adds it to your Startup folder so it runs when you sign in.
setlocal
cd /d "%~dp0"
call build.cmd || exit /b 1

set DEST=%LOCALAPPDATA%\TeamsMicHold
taskkill /im TeamsMicHold.exe /f >nul 2>&1
timeout /t 1 /nobreak >nul
if not exist "%DEST%" mkdir "%DEST%"
copy /y bin\TeamsMicHold.exe "%DEST%\" >nul || exit /b 1
copy /y bin\CaptureProbe.exe "%DEST%\" >nul || exit /b 1

powershell -NoProfile -Command ^
  "$s = (New-Object -ComObject WScript.Shell).CreateShortcut([Environment]::GetFolderPath('Startup') + '\TeamsMicHold.lnk');" ^
  "$s.TargetPath = '%DEST%\TeamsMicHold.exe'; $s.Save()" || exit /b 1

start "" "%DEST%\TeamsMicHold.exe"
echo Installed to %DEST% and added to Startup. Look for the shield icon in the system tray.

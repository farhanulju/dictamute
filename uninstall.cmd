@echo off
rem Stops TeamsMicHold, removes it from Startup, and deletes %LOCALAPPDATA%\TeamsMicHold.
setlocal
taskkill /im TeamsMicHold.exe >nul 2>&1
timeout /t 2 /nobreak >nul
taskkill /im TeamsMicHold.exe /f >nul 2>&1
powershell -NoProfile -Command ^
  "Remove-Item -ErrorAction SilentlyContinue ([Environment]::GetFolderPath('Startup') + '\TeamsMicHold.lnk')"
if exist "%LOCALAPPDATA%\TeamsMicHold" rmdir /s /q "%LOCALAPPDATA%\TeamsMicHold"
echo TeamsMicHold removed.

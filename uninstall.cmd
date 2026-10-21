@echo off
rem Stops DictaMute, turns off "Start with Windows", and deletes %LOCALAPPDATA%\DictaMute
rem (the installed copy, its log and settings). A downloaded DictaMute.exe elsewhere is left alone.
setlocal
taskkill /im DictaMute.exe >nul 2>&1
timeout /t 2 /nobreak >nul
taskkill /im DictaMute.exe /f >nul 2>&1
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v DictaMute /f >nul 2>&1
if exist "%LOCALAPPDATA%\DictaMute" rmdir /s /q "%LOCALAPPDATA%\DictaMute"
echo DictaMute removed.

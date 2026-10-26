@echo off
rem Builds bin\DictaMute.exe and bin\CaptureProbe.exe with the .NET Framework
rem compiler that ships with Windows. No SDK or downloads needed.
setlocal
cd /d "%~dp0"
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
if not exist "%FW%\csc.exe" (
  echo Could not find %FW%\csc.exe. DictaMute needs 64-bit Windows with .NET Framework 4.x.
  exit /b 1
)
if not exist bin mkdir bin

rem The exe's icon is drawn by the same code as the tray icons (src\Icons.cs).
"%FW%\csc.exe" /nologo /target:exe /out:bin\MakeIcon.exe /r:System.Drawing.dll ^
  src\MakeIcon.cs src\Icons.cs || exit /b 1
bin\MakeIcon.exe bin\DictaMute.ico || exit /b 1

"%FW%\csc.exe" /nologo /optimize /target:winexe /out:bin\DictaMute.exe /win32icon:bin\DictaMute.ico ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll /r:System.Web.Extensions.dll ^
  /r:"%FW%\WPF\UIAutomationClient.dll" /r:"%FW%\WPF\UIAutomationTypes.dll" /r:"%FW%\WPF\WindowsBase.dll" ^
  src\DictaMute.cs src\Settings.cs src\SettingsForm.cs src\CallApps.cs src\WisprState.cs src\Icons.cs src\AudioCapture.cs || exit /b 1
"%FW%\csc.exe" /nologo /optimize /target:exe /out:bin\CaptureProbe.exe ^
  src\CaptureProbe.cs src\AudioCapture.cs || exit /b 1
echo Built bin\DictaMute.exe

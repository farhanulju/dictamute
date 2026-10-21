@echo off
rem Builds bin\TeamsMicHold.exe and bin\CaptureProbe.exe with the .NET Framework
rem compiler that ships with Windows. No SDK or downloads needed.
setlocal
cd /d "%~dp0"
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
if not exist "%FW%\csc.exe" (
  echo Could not find %FW%\csc.exe. TeamsMicHold needs 64-bit Windows with .NET Framework 4.x.
  exit /b 1
)
if not exist bin mkdir bin
"%FW%\csc.exe" /nologo /optimize /target:winexe /out:bin\TeamsMicHold.exe ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  /r:"%FW%\WPF\UIAutomationClient.dll" /r:"%FW%\WPF\UIAutomationTypes.dll" /r:"%FW%\WPF\WindowsBase.dll" ^
  src\TeamsMicHold.cs src\AudioCapture.cs || exit /b 1
"%FW%\csc.exe" /nologo /optimize /target:exe /out:bin\CaptureProbe.exe ^
  src\CaptureProbe.cs src\AudioCapture.cs || exit /b 1
echo Built bin\TeamsMicHold.exe

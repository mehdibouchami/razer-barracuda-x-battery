@echo off
rem Builds with the C# compiler that ships with Windows (.NET Framework 4.x) - no SDK needed.
setlocal
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist bin mkdir bin

rem A running exe is locked by Windows: close the tray app now, restart it after the build.
set RESTART=
tasklist /fi "imagename eq BarracudaBattery.exe" 2>nul | find /i "BarracudaBattery.exe" >nul && (
  set RESTART=1
  taskkill /f /im BarracudaBattery.exe >nul
  ping -n 2 127.0.0.1 >nul
)

"%CSC%" /nologo /optimize /platform:x64 /target:exe /out:bin\probe.exe ^
  src\AppInfo.cs src\Hid.cs src\Protocol.cs src\Probe.cs || exit /b 1

"%CSC%" /nologo /optimize /platform:x64 /target:winexe /out:bin\BarracudaBattery.exe /win32icon:assets\app.ico ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  src\AppInfo.cs src\Hid.cs src\Protocol.cs src\TrayApp.cs || exit /b 1

echo Built bin\probe.exe and bin\BarracudaBattery.exe
if defined RESTART start "" bin\BarracudaBattery.exe

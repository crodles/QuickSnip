@echo off
rem Builds QuickSnip.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
rem The text extractor uses Windows' built-in text recognition (WinMetadata files that come with Windows 10/11).
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
if not exist "%FW%\csc.exe" set FW=%WINDIR%\Microsoft.NET\Framework\v4.0.30319
set WM=%WINDIR%\System32\WinMetadata
set OUT=%~dp0QuickSnip.exe
if not "%~1"=="" set OUT=%~1
"%FW%\csc.exe" /nologo /target:winexe /optimize+ /out:"%OUT%" ^
  /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  /r:"%FW%\System.Runtime.dll" /r:"%FW%\System.Threading.Tasks.dll" /r:"%FW%\System.Runtime.InteropServices.WindowsRuntime.dll" ^
  /r:"%FW%\System.Runtime.WindowsRuntime.dll" ^
  /r:"%WM%\Windows.Foundation.winmd" /r:"%WM%\Windows.Graphics.winmd" /r:"%WM%\Windows.Media.winmd" ^
  /r:"%WM%\Windows.Storage.winmd" /r:"%WM%\Windows.Globalization.winmd" /r:"%WM%\Windows.ApplicationModel.winmd" ^
  "%~dp0QuickSnip.cs"
if errorlevel 1 (echo Build FAILED & exit /b 1)
echo Built "%OUT%"

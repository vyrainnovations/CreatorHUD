@echo off
rem Builds CreatorHUD.exe: Eshan's fork of WinUltra (MIT, by Gamindu Sasmitha) with live liquid glass,
rem click tabs with a refracting capsule and a Blender render ring.
rem Needs only what ships with Windows 11: the .NET Framework compiler and the WinRT metadata in System32.
cd /d "%~dp0"
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
set WPF=%FW%\WPF
set WM=%WINDIR%\System32\WinMetadata
set WINMD=
for %%f in ("%WM%\*.winmd") do call set WINMD=%%WINMD%% /r:"%%f"
if not exist WinUltra.ico powershell -NoProfile -File MakeIcon.ps1
"%FW%\csc.exe" /nologo /optimize+ /target:winexe /platform:x64 /define:FRIENDS /out:CreatorHUD.exe /win32icon:WinUltra.ico /win32manifest:WinUltra.manifest ^
 /r:"%WPF%\PresentationFramework.dll" /r:"%WPF%\PresentationCore.dll" /r:"%WPF%\WindowsBase.dll" /r:"%FW%\System.Xaml.dll" ^
 /r:"%FW%\System.Windows.Forms.dll" /r:"%FW%\System.Drawing.dll" ^
 /r:"%FW%\System.Management.dll" /r:"%FW%\System.Web.Extensions.dll" /r:"%FW%\System.IO.Compression.dll" /r:"%FW%\System.IO.Compression.FileSystem.dll" ^
 /r:"%FW%\System.Runtime.dll" /r:"%FW%\System.Runtime.WindowsRuntime.dll" /r:"%FW%\System.Runtime.InteropServices.WindowsRuntime.dll" ^
 %WINMD% ^
 /resource:WinUltra.ico,WinUltra.ico ^
 src\WinUltra.cs src\Island.cs src\Setup.cs src\Glass.cs src\RenderWatch.cs src\WinRT.cs

@echo off
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo csc.exe not found: %CSC%
  exit /b 1
)
if not exist publish mkdir publish
if not exist docs\assets mkdir docs\assets
"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ /out:publish\CaptureAssets.exe /main:CaptureAssets /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll Standalone.cs tools\CaptureAssets.cs
if errorlevel 1 exit /b 1
publish\CaptureAssets.exe docs\assets

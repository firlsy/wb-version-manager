@echo off
rem ============================================================
rem  WorkBuddy Version Manager - C# client build script
rem  Requires: .NET Framework 4.x (preinstalled on Windows 10/11)
rem  Output:   dist\WorkBuddyVersionManager.exe (single file)
rem ============================================================
setlocal

set ROOT=%~dp0
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" goto nocsc

set OUTDIR=%ROOT%..\dist
if not exist "%OUTDIR%" mkdir "%OUTDIR%"

set RES=
if exist "%ROOT%..\data\catalog.json" set RES=/resource:"%ROOT%..\data\catalog.json",catalog.json

echo [*] compiler %CSC%
echo [*] output   %OUTDIR%\WorkBuddyVersionManager.exe

"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 /out:"%OUTDIR%\WorkBuddyVersionManager.exe" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Net.Http.dll /r:System.Web.Extensions.dll %RES% "%ROOT%src\Json.cs" "%ROOT%src\Core.cs" "%ROOT%src\StateStore.cs" "%ROOT%src\Detector.cs" "%ROOT%src\Catalog.cs" "%ROOT%src\Block.cs" "%ROOT%src\Download.cs" "%ROOT%src\Install.cs" "%ROOT%src\Theme.cs" "%ROOT%src\NotesView.cs" "%ROOT%src\MainForm.cs" "%ROOT%src\Program.cs"
if errorlevel 1 goto failed

for %%F in ("%OUTDIR%\WorkBuddyVersionManager.exe") do echo [ok] built %%~zF bytes
exit /b 0

:nocsc
echo [x] .NET Framework compiler not found. Install .NET Framework 4.8 first.
exit /b 1

:failed
echo [x] build failed
exit /b 1

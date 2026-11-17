@echo off
rem ============================================================
rem  WorkBuddy Version Manager - C# client build script
rem  Produces two single-file builds:
rem    dist\WorkBuddyVersionManager-arm64.exe  AnyCPU, JITs to native
rem                                            ARM64 on Windows on ARM
rem    dist\WorkBuddyVersionManager-x86.exe    32-bit, runs on x86,
rem                                            x64 and ARM64 (emulation)
rem  Requires: .NET Framework 4.x (preinstalled on Windows 10/11)
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

set REFS=/r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Net.Http.dll /r:System.Web.Extensions.dll
set SRC="%ROOT%src\Json.cs" "%ROOT%src\Core.cs" "%ROOT%src\StateStore.cs" "%ROOT%src\Detector.cs" "%ROOT%src\Catalog.cs" "%ROOT%src\Block.cs" "%ROOT%src\Download.cs" "%ROOT%src\Install.cs" "%ROOT%src\Theme.cs" "%ROOT%src\NotesView.cs" "%ROOT%src\MainForm.cs" "%ROOT%src\Program.cs"

echo [*] compiler %CSC%

"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 /out:"%OUTDIR%\WorkBuddyVersionManager-arm64.exe" %REFS% %RES% %SRC%
if errorlevel 1 goto failed

"%CSC%" /nologo /target:winexe /platform:x86 /optimize+ /codepage:65001 /out:"%OUTDIR%\WorkBuddyVersionManager-x86.exe" %REFS% %RES% %SRC%
if errorlevel 1 goto failed

for %%F in ("%OUTDIR%\WorkBuddyVersionManager-arm64.exe") do echo [ok] arm64 %%~zF bytes
for %%F in ("%OUTDIR%\WorkBuddyVersionManager-x86.exe") do echo [ok] x86   %%~zF bytes
exit /b 0

:nocsc
echo [x] .NET Framework compiler not found. Install .NET Framework 4.8 first.
exit /b 1

:failed
echo [x] build failed
exit /b 1

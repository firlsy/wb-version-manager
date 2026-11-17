@echo off
rem ============================================================
rem  WB Version Manager - C# client build script
rem
rem  Produces ONE universal single-file build:
rem    dist\WB-Version-Manager-universal-v<version>.exe
rem
rem  /platform:anycpu + ILONLY means the CLR picks the right
rem  bittiness at load time:
rem    - 32-bit Windows  -> runs as 32-bit
rem    - 64-bit Windows  -> runs as 64-bit
rem    - Windows on ARM  -> JITs to native ARM64
rem  So a single file covers x86 / x64 / ARM64.
rem
rem  The version in the file name is read from
rem  client\src\Core.cs (AppInfo.Version), single source of truth.
rem
rem  Requires: .NET Framework 4.x (preinstalled on Windows 10/11)
rem ============================================================
setlocal

set ROOT=%~dp0
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" goto nocsc

set OUTDIR=%ROOT%..\dist
if not exist "%OUTDIR%" mkdir "%OUTDIR%"

rem ---------- version ----------
set RAW=
for /f "tokens=2 delims==" %%A in ('findstr /c:"public const string Version" "%ROOT%src\Core.cs"') do set RAW=%%A
if "%RAW%"=="" goto nover
set VER=%RAW:"=%
set VER=%VER:;=%
set VER=%VER: =%
if "%VER%"=="" goto nover

set NAME=WB-Version-Manager-universal-v%VER%.exe
set OUT=%OUTDIR%\%NAME%

rem ---------- resources ----------
set RES=
if exist "%ROOT%..\data\catalog.json" set RES=/resource:"%ROOT%..\data\catalog.json",catalog.json

rem ---------- icon ----------
set ICON=
if exist "%ROOT%app.ico" set ICON=/win32icon:"%ROOT%app.ico"

set REFS=/r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Net.Http.dll /r:System.Web.Extensions.dll
set SRC="%ROOT%src\Json.cs" "%ROOT%src\Core.cs" "%ROOT%src\StateStore.cs" "%ROOT%src\Detector.cs" "%ROOT%src\Catalog.cs" "%ROOT%src\Block.cs" "%ROOT%src\Download.cs" "%ROOT%src\Install.cs" "%ROOT%src\Theme.cs" "%ROOT%src\NotesView.cs" "%ROOT%src\MainForm.cs" "%ROOT%src\Program.cs"

echo [*] compiler %CSC%
echo [*] version  %VER%
if not "%ICON%"=="" echo [*] icon     app.ico

"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 /out:"%OUT%" %ICON% %REFS% %RES% %SRC%
if errorlevel 1 goto failed

for %%F in ("%OUT%") do echo [ok] %NAME%  %%~zF bytes
exit /b 0

:nocsc
echo [x] .NET Framework compiler not found. Install .NET Framework 4.8 first.
exit /b 1

:nover
echo [x] cannot read AppInfo.Version from src\Core.cs
exit /b 1

:failed
echo [x] build failed
exit /b 1

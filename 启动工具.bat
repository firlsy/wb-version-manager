@echo off
chcp 65001 >nul
cd /d "%~dp0"

where node >nul 2>nul
if errorlevel 1 (
  echo.
  echo   需要 Node.js 20 或更高版本：https://nodejs.org/
  echo.
  pause
  exit /b 1
)

echo.
echo   正在启动 WB 版本管理器...
echo   浏览器会自动打开 http://127.0.0.1:17321
echo   关闭此窗口即停止工具。
echo.

node server.js
pause

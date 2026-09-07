@echo off
setlocal EnableExtensions
cd /d "%~dp0"

echo ======================================================================
echo Six Stars native AI runtime setup
echo Installs/validates llama-server, whisper.cpp, and qwentts.cpp native packs.
echo ======================================================================
echo.
echo The normal runtime does not create a Python environment.
echo For the NVIDIA redist, package the required native runtime DLLs beside
echo each engine. End users then need a compatible NVIDIA driver; they do not
echo need the full CUDA Toolkit merely to run the packaged app.
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Install-AiRuntime.ps1 %*
if errorlevel 1 exit /b %ERRORLEVEL%

echo.
echo Setup complete. Run:
echo   launch.cmd
pause

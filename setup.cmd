@echo off
setlocal EnableExtensions
cd /d "%~dp0"

echo ======================================================================
echo Six Stars AI runtime setup
echo Builds/installs llama.cpp, faster-whisper, and Qwen3-TTS runtime packs.
echo ======================================================================

echo This can take a long time the first time because it may build llama.cpp,
echo create Python runtime packs, install CUDA packages, and download models.
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Install-AiRuntime.ps1 %*
if errorlevel 1 exit /b %ERRORLEVEL%

echo.
echo Setup complete. Run:
echo   launch.cmd
pause

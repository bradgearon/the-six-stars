@echo off
setlocal EnableExtensions
cd /d "%~dp0"

rem Development/source-services flow: starts source/venv services in a separate
rem window, then opens the UI. The normal run-ai-companion.cmd uses the
rem integrated prebuilt engine launcher instead.
echo Starting Six Stars source/venv AI services in a separate window, then launching the desktop UI.
echo Services will use the repo .venv for Python ASR/TTS and the llama.cpp engine for the local LLM.
echo.

start "Six Stars Source AI Services" cmd /k "cd /d ""%~dp0"" && run-ai-source-services.cmd %*"

echo Waiting a few seconds before opening the UI...
timeout /t 6 /nobreak >nul
call launch-ui-only.cmd

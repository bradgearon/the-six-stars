@echo off
setlocal EnableExtensions
cd /d "%~dp0"

rem Services-only launcher for the prebuilt runtime engine EXEs.
rem This does NOT launch the WPF desktop app and does NOT use the repo .venv
rem for ASR/TTS. It uses the same prebuilt engine runtime path as
rem run-ai-companion.cmd, but passes --no-ui.
echo Starting Six Stars prebuilt AI engine services only.
echo The desktop app will NOT be launched.
echo This uses the engine EXEs from config\runtime-manifest.json.
echo Leave this window open while using launch-ui-only.cmd or the Android bridge.
echo.

call launch.cmd --skip-install --no-ui %*

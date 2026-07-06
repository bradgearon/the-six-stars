@echo off
setlocal EnableExtensions
cd /d "%~dp0"

rem Launch only the WPF desktop UI. Use this after run-ai-companion.cmd has already
rem started the local LLM, Whisper, and Qwen TTS services in another window.
set SIX_STARS_LLM_ENDPOINT=http://127.0.0.1:8080/v1/chat/completions
set SIX_STARS_SCREEN_LLM_ENDPOINT=http://127.0.0.1:8080/v1/chat/completions
set SIX_STARS_LLM_MODEL=local-model
set SIX_STARS_SCREEN_LLM_MODEL=local-model
set SIX_STARS_SCREEN_LLM_ENABLED=true
set SIX_STARS_WHISPER_ENDPOINT=http://127.0.0.1:8000/v1/audio/transcriptions
set SIX_STARS_WEBSOCKET_TTS_URL=ws://127.0.0.1:8100/tts/stream
set SIX_STARS_USE_STREAMING_TTS=true
set SIX_STARS_MOBILE_BRIDGE_ENABLED=true

if exist "six-stars.exe" (
  start "Six Stars" "%CD%\six-stars.exe"
  exit /b 0
)

if exist "ui\six-stars.exe" (
  start "Six Stars" "%CD%\ui\six-stars.exe"
  exit /b 0
)

if exist "src\Wds.SixStars.Ui\Wds.SixStars.Ui.csproj" (
  dotnet run --project src\Wds.SixStars.Ui\Wds.SixStars.Ui.csproj
  exit /b %ERRORLEVEL%
)

echo Could not find WPF UI executable or source project.
exit /b 1

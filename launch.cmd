@echo off
setlocal EnableExtensions
cd /d "%~dp0"

rem New default: run the integrated launcher that starts llama.cpp, faster-whisper,
rem and Qwen3-TTS from redistributable runtime packs, then opens the WPF UI.
if exist "redist\six-stars-launcher.exe" (
  cd /d "redist"
  six-stars-launcher.exe %*
  exit /b %ERRORLEVEL%
)

if exist "src\Wds.SixStars.Launcher\Wds.SixStars.Launcher.csproj" (
  dotnet run --project src\Wds.SixStars.Launcher\Wds.SixStars.Launcher.csproj -- %*
  exit /b %ERRORLEVEL%
)

echo Launcher project not found. Run build-ai-redist.cmd first or restore the source tree.
exit /b 1

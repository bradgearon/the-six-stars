@echo off
setlocal EnableExtensions
cd /d "%~dp0"

rem Integrated/prebuilt launcher path.
rem Preferred for normal use: starts local LLM/ASR/TTS engine services from the
rem redistributable runtime packs, then opens the WPF UI.
if exist "redist\six-stars-launcher.exe" (
  cd /d "redist"
  six-stars-launcher.exe %*
  exit /b %ERRORLEVEL%
)

rem Source fallback for development zips where redist\six-stars-launcher.exe has
rem not been built yet. This still uses the launcher project and engine packs.
if exist "src\Wds.SixStars.Launcher\Wds.SixStars.Launcher.csproj" (
  dotnet run --project src\Wds.SixStars.Launcher\Wds.SixStars.Launcher.csproj -- %*
  exit /b %ERRORLEVEL%
)

echo Launcher project not found. Run build-ai-redist.cmd first or restore the source tree.
exit /b 1

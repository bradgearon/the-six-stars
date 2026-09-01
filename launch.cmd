@echo off
setlocal EnableExtensions
cd /d "%~dp0"

rem Native launcher path: no Python/venv is required.
if exist "six-stars-launcher.exe" (
  "six-stars-launcher.exe" %*
  exit /b %ERRORLEVEL%
)

if exist "src\Wds.SixStars.Launcher\bin\Release\net8.0\six-stars-launcher.exe" (
  "src\Wds.SixStars.Launcher\bin\Release\net8.0\six-stars-launcher.exe" %*
  exit /b %ERRORLEVEL%
)

where dotnet >nul 2>nul
if errorlevel 1 (
  echo ERROR: six-stars-launcher.exe was not found and dotnet is not installed.
  echo Build/publish the launcher or use the native redistribution package.
  exit /b 1
)

dotnet run --project "src\Wds.SixStars.Launcher\Wds.SixStars.Launcher.csproj" -- %*

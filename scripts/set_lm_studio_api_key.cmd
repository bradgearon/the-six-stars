@echo off
setlocal EnableExtensions DisableDelayedExpansion
cd /d "%~dp0\.."

if "%~1"=="" (
  set /p "SIX_STARS_TOKEN=Paste LM Studio API token: "
) else (
  set "SIX_STARS_TOKEN=%~1"
)

if "%SIX_STARS_TOKEN%"=="" (
  echo No token was provided.
  exit /b 1
)

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ErrorActionPreference='Stop';" ^
  "$token=$env:SIX_STARS_TOKEN;" ^
  "$ui=Join-Path (Get-Location) 'src\Wds.SixStars.Ui';" ^
  "$json=[ordered]@{ LLM=[ordered]@{ ApiKey=$token }; ScreenLLM=[ordered]@{ ApiKey=$token } } | ConvertTo-Json -Depth 8;" ^
  "Set-Content -Path (Join-Path $ui 'appsettings.Local.json') -Value $json -Encoding UTF8;" ^
  "$bin=Join-Path $ui 'bin\Debug\net8.0-windows';" ^
  "if (Test-Path $bin) { Set-Content -Path (Join-Path $bin 'appsettings.Local.json') -Value $json -Encoding UTF8 };" ^
  "Write-Host 'Wrote src\Wds.SixStars.Ui\appsettings.Local.json';" ^
  "if (Test-Path $bin) { Write-Host 'Wrote bin\Debug\net8.0-windows\appsettings.Local.json' }"

if errorlevel 1 exit /b %errorlevel%

echo.
echo Done. appsettings.Local.json is gitignored and will override appsettings.json without being overwritten by future patch zips.
echo Do not commit this file.

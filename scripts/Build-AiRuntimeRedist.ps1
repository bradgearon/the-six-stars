param(
  [ValidateSet("auto", "cpu", "nvidia-cu12")][string]$Profile = "auto",
  [switch]$SkipRuntimeInstall,
  [switch]$SkipModels,
  [switch]$Force
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
Set-Location $Root

if (!$SkipRuntimeInstall) {
  & "$PSScriptRoot/Install-AiRuntime.ps1" -Profile $Profile -Force:$Force -SkipModels:$SkipModels
}

Write-Host "Publishing WPF UI..."
dotnet publish "src/Wds.SixStars.Ui/Wds.SixStars.Ui.csproj" -c Release -r win-x64 --self-contained false -o "redist/ui"

Write-Host "Publishing launcher..."
dotnet publish "src/Wds.SixStars.Launcher/Wds.SixStars.Launcher.csproj" -c Release -r win-x64 --self-contained false -o "redist"

Write-Host "Copying runtime config, scripts, engines, models, and notices..."
New-Item -ItemType Directory -Force -Path "redist/config", "redist/scripts", "redist/licenses", "redist/models", "redist/engines", "redist/logs/services" | Out-Null
Copy-Item "config/*.json" "redist/config" -Force
Copy-Item "licenses/*" "redist/licenses" -Force -ErrorAction SilentlyContinue
Copy-Item "scripts/Install-AiRuntime.ps1", "scripts/Download-AiModels.ps1" "redist/scripts" -Force
if (Test-Path "engines") { Copy-Item "engines/*" "redist/engines" -Recurse -Force }
if (Test-Path "models/README.txt") { Copy-Item "models/README.txt" "redist/models" -Force }

@"
@echo off
setlocal EnableExtensions
cd /d "%~dp0"
six-stars-launcher.exe --skip-install
echo.
pause
"@ | Set-Content "redist/Launch Six Stars AI Companion.cmd" -Encoding ASCII

$zip = "redist/six-stars-ai-companion-redist.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "redist/*" -DestinationPath $zip -Force
Write-Host "Redist ready: $zip" -ForegroundColor Green

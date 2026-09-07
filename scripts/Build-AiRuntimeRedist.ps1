param(
  [ValidateSet("auto", "cpu", "nvidia", "nvidia-cu12")][string]$Profile = "auto",
  [switch]$SkipRuntimeInstall,
  [switch]$SkipModels,
  [switch]$BundleModels,
  [switch]$Force,
  [string]$RuntimeSource = "runtime-packs/win-x64"
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
Set-Location $Root

if (!$SkipRuntimeInstall) {
  & "$PSScriptRoot/Install-AiRuntime.ps1" -Profile $Profile -Force:$Force -SkipModels:$SkipModels -RuntimeSource $RuntimeSource
}

Write-Host "Publishing WPF UI..."
dotnet publish "src/Wds.SixStars.Ui/Wds.SixStars.Ui.csproj" -c Release -r win-x64 --self-contained true -o "redist/ui"

Write-Host "Publishing launcher..."
dotnet publish "src/Wds.SixStars.Launcher/Wds.SixStars.Launcher.csproj" -c Release -r win-x64 --self-contained true -o "redist"

Write-Host "Copying native runtime config, engines, and notices..."
# Do not accidentally carry giant model weights forward from an older redist build.
if (Test-Path "redist/models") { Remove-Item "redist/models" -Recurse -Force }
New-Item -ItemType Directory -Force -Path "redist/config", "redist/scripts", "redist/licenses", "redist/models", "redist/engines", "redist/logs/services" | Out-Null
Copy-Item "config/*.json" "redist/config" -Force
Copy-Item "licenses/*" "redist/licenses" -Force -ErrorAction SilentlyContinue
Copy-Item "scripts/Install-AiRuntime.ps1", "scripts/Download-AiModels.ps1" "redist/scripts" -Force
if (Test-Path "engines") { Copy-Item "engines/*" "redist/engines" -Recurse -Force }

# The normal public redist intentionally stays small: model weights are fetched on first launch
# from config/models.json. Use -BundleModels only for an offline/internal package.
if (Test-Path "models/README.txt") { Copy-Item "models/README.txt" "redist/models/README.txt" -Force }
if (Test-Path "models/voice_clones") { Copy-Item "models/voice_clones" "redist/models/voice_clones" -Recurse -Force }
if ($BundleModels -and (Test-Path "models")) {
  Write-Host "Bundling local model files because -BundleModels was supplied."
  Copy-Item "models/*" "redist/models" -Recurse -Force
} else {
  Write-Host "Model weights are not bundled; the launcher will download missing weights on first launch."
}

@"
@echo off
setlocal EnableExtensions
cd /d "%~dp0"
six-stars-launcher.exe --skip-install
if errorlevel 1 pause
"@ | Set-Content "redist/Launch Six Stars AI Companion.cmd" -Encoding ASCII

$zip = "redist/six-stars-ai-companion-native-redist.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "redist/*" -DestinationPath $zip -Force
Write-Host "Native redist ready: $zip" -ForegroundColor Green

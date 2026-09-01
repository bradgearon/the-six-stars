param(
  [string]$ModelsConfig = "config/models.json",
  [switch]$SkipTts,
  [switch]$SkipAsr,
  [switch]$SkipLlm,
  [switch]$VerifyNative,
  [switch]$AllowMissing
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
Set-Location $Root

if ($SkipTts -or $SkipAsr -or $SkipLlm) {
  Write-Warning "Per-model skip switches are legacy options and are ignored by the new manifest-driven downloader. Existing model files are never downloaded again."
}

$launcher = Join-Path $Root "six-stars-launcher.exe"
if (Test-Path $launcher) {
  & $launcher --download-models-only --models $ModelsConfig
  exit $LASTEXITCODE
}

$releaseLauncher = Join-Path $Root "src/Wds.SixStars.Launcher/bin/Release/net8.0/six-stars-launcher.exe"
if (Test-Path $releaseLauncher) {
  & $releaseLauncher --download-models-only --models $ModelsConfig
  exit $LASTEXITCODE
}

if (!(Get-Command dotnet -ErrorAction SilentlyContinue)) {
  throw "six-stars-launcher.exe was not found and dotnet is not installed. Build/publish Wds.SixStars.Launcher first."
}

& dotnet run --project "src/Wds.SixStars.Launcher/Wds.SixStars.Launcher.csproj" -- --download-models-only --models $ModelsConfig
exit $LASTEXITCODE

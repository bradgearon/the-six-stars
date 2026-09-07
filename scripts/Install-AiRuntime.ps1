param(
  [ValidateSet("auto", "cpu", "nvidia", "nvidia-cu12")][string]$Profile = "auto",
  [switch]$Force,
  # Kept for compatibility with older launch scripts. This installer stages prebuilt native packs; it does not rebuild them by default.
  [switch]$SkipLlamaBuild,
  [switch]$SkipPythonPacks,
  [switch]$SkipModels,
  [string]$RuntimeSource = "runtime-packs/win-x64"
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
Set-Location $Root

function Have-Nvidia() {
  try {
    $null = & nvidia-smi --query-gpu=name --format=csv,noheader 2>$null
    return $LASTEXITCODE -eq 0
  } catch { return $false }
}

$resolvedProfile = $Profile
if ($resolvedProfile -eq "auto") {
  $resolvedProfile = if (Have-Nvidia) { "nvidia" } else { "cpu" }
}

Write-Host "Six Stars native AI runtime profile: $resolvedProfile"
Write-Host "Runtime source: $RuntimeSource"

if ($resolvedProfile -eq "cpu") {
  throw "This native Six Stars runtime manifest is CUDA/NVIDIA-only. Use a hosted service or add CPU/Vulkan packs to config/runtime-manifest.json."
}

if (!(Have-Nvidia)) {
  throw "No usable NVIDIA driver was detected (nvidia-smi failed). Install/update the NVIDIA display driver first. The full CUDA Toolkit is not required when the redistributed runtime pack includes its CUDA runtime DLLs."
}

New-Item -ItemType Directory -Force -Path "engines", "models", "logs/services" | Out-Null

$sourceRoot = if ([IO.Path]::IsPathRooted($RuntimeSource)) { $RuntimeSource } else { Join-Path $Root $RuntimeSource }

$packs = @(
  @{ Name = "llama.cpp";   Source = "llama-cpp-dynamic-win-x64";         Destination = "engines/llm/llama-cpp-dynamic-win-x64";              Required = "llama-server.exe" },
  @{ Name = "whisper.cpp"; Source = "whispercpp-nvidia-win-x64";         Destination = "engines/asr/whispercpp-nvidia-win-x64";              Required = "whisper-server.exe" },
  @{ Name = "qwentts.cpp"; Source = "qwenttscpp-nvidia-win-x64";         Destination = "engines/tts/qwenttscpp-nvidia-win-x64";              Required = "tts-server.exe" }
)

$missing = @()
foreach ($pack in $packs) {
  $dest = Join-Path $Root $pack.Destination
  $required = Join-Path $dest $pack.Required
  $src = Join-Path $sourceRoot $pack.Source

  if ($Force -and (Test-Path $dest) -and (Test-Path $src)) {
    Remove-Item $dest -Recurse -Force
  }

  if (!(Test-Path $required)) {
    if (Test-Path $src) {
      Write-Host "Installing $($pack.Name) native pack -> $($pack.Destination)"
      New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dest) | Out-Null
      Copy-Item $src $dest -Recurse -Force
    }
  }

  if (!(Test-Path $required)) {
    $missing += "$($pack.Name): $required"
  } else {
    Write-Host "Ready: $($pack.Name) ($required)"
  }
}

if ($missing.Count -gt 0) {
  Write-Host ""
  Write-Host "Missing native runtime files:" -ForegroundColor Yellow
  $missing | ForEach-Object { Write-Host "  $_" }
  Write-Host ""
  Write-Host "Place the prebuilt redistribution folders under '$sourceRoot' or directly under engines/."
  Write-Host "No Python environment is required by this runtime."
  throw "Native runtime pack is incomplete."
}

if (!$SkipModels) {
  Write-Host "Model download is handled by six-stars-launcher from config/models.json using bezzad/Downloader."
  Write-Host "Run launch.cmd --download-models-only if you only want to fetch model files."
}

Write-Host ""
Write-Host "Native AI runtime ready." -ForegroundColor Green
Write-Host "Runtime requirement: compatible NVIDIA driver. A CUDA Toolkit install is only needed if your particular redistributed binaries were built to depend on CUDA DLLs that you did not ship beside them."

param(
  [ValidateSet("auto", "cpu", "nvidia-cu12")][string]$Profile = "auto",
  [switch]$Force,
  [switch]$SkipLlamaBuild,
  [switch]$SkipPythonPacks,
  [switch]$SkipModels
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
if ($Profile -eq "auto") {
  $resolvedProfile = if (Have-Nvidia) { "nvidia-cu12" } else { "cpu" }
}

Write-Host "Six Stars AI runtime install profile: $resolvedProfile"
New-Item -ItemType Directory -Force -Path "engines", "models", "logs/services" | Out-Null

if (!$SkipLlamaBuild) {
  $llamaExe = "engines/llm/llama-cpp-dynamic-win-x64/llama-server.exe"
  if ($Force -or !(Test-Path $llamaExe)) {
    & "$PSScriptRoot/Build-LlamaCppDynamic.ps1" -WithCuda:($resolvedProfile -eq "nvidia-cu12") -WithVulkan:$true
  } else {
    Write-Host "llama.cpp dynamic runtime already exists: $llamaExe"
  }
}

if (!$SkipPythonPacks) {
  $asrCpu = "engines/asr/faster-whisper-cpu-win-x64/faster_whisper_openai_server.exe"
  if ($Force -or !(Test-Path $asrCpu)) { & "$PSScriptRoot/Build-PythonRuntimePack.ps1" -Server asr -Profile cpu }

  $ttsCpu = "engines/tts/qwen3-tts-cpu-win-x64/websocket_tts_server.exe"
  if ($Force -or !(Test-Path $ttsCpu)) { & "$PSScriptRoot/Build-PythonRuntimePack.ps1" -Server tts -Profile cpu }

  if ($resolvedProfile -eq "nvidia-cu12") {
    $asrNvidia = "engines/asr/faster-whisper-nvidia-cu12-win-x64/faster_whisper_openai_server.exe"
    if ($Force -or !(Test-Path $asrNvidia)) { & "$PSScriptRoot/Build-PythonRuntimePack.ps1" -Server asr -Profile nvidia-cu12 }

    $ttsNvidia = "engines/tts/qwen3-tts-nvidia-cu12-win-x64/websocket_tts_server.exe"
    if ($Force -or !(Test-Path $ttsNvidia)) { & "$PSScriptRoot/Build-PythonRuntimePack.ps1" -Server tts -Profile nvidia-cu12 }
  }
}

if (!$SkipModels) {
  & "$PSScriptRoot/Download-AiModels.ps1"
}

Write-Host "AI runtime install complete." -ForegroundColor Green

param(
  [string]$ModelsConfig = "config/models.json",
  [switch]$SkipTts,
  [switch]$SkipAsr,
  [switch]$SkipLlm
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
Set-Location $Root

$config = Get-Content $ModelsConfig -Raw | ConvertFrom-Json
New-Item -ItemType Directory -Force -Path "models/llm", "models/asr", "models/tts", "models/hf" | Out-Null

function Have-Command($name) {
  return [bool](Get-Command $name -ErrorAction SilentlyContinue)
}

if (!(Have-Command "huggingface-cli")) {
  Write-Host "huggingface-cli was not found. Installing into current Python environment..."
  python -m pip install -U "huggingface-hub[cli]"
}

if (!$SkipAsr) {
  $asrName = $config.models.asr.modelName
  if ($asrName) {
    Write-Host "Preparing ASR model cache for faster-whisper: $asrName"
    Write-Host "faster-whisper can also auto-download this on first use into models/asr."
  }
}

if (!$SkipTts) {
  $ttsName = $config.models.tts.modelName
  $ttsLocal = $config.models.tts.localPath
  if ($ttsName -and $ttsLocal) {
    Write-Host "Downloading TTS model $ttsName to $ttsLocal"
    huggingface-cli download $ttsName --local-dir $ttsLocal --local-dir-use-symlinks False
  }
}

if (!$SkipLlm) {
  $llm = $config.models.llm
  if ($llm.preferredMode -eq "llama-hf") {
    Write-Host "LLM is configured for llama.cpp -hf: $($llm.hfSpecifier)"
    Write-Host "llama-server will download/cache it on first run. To force an offline local GGUF, place it at: $($llm.localPath)"
  } elseif ($llm.localPath) {
    Write-Host "LLM localPath configured: $($llm.localPath)"
  }
}

Write-Host "Model preparation complete." -ForegroundColor Green

param(
  [ValidateSet("asr", "tts")][string]$Server,
  [ValidateSet("cpu", "nvidia-cu12")][string]$Profile = "cpu",
  [string]$Python = "py -3.11",
  [switch]$Force
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
Set-Location $Root

$venvDir = "build/python-packs/$Server-$Profile/.venv"
$distName = if ($Server -eq "asr") { "faster_whisper_openai_server" } else { "websocket_tts_server" }
$scriptPath = if ($Server -eq "asr") { "scripts/faster_whisper_openai_server.py" } else { "scripts/websocket_tts_server.py" }
$outDir = if ($Server -eq "asr") { "engines/asr/faster-whisper-$Profile-win-x64" } else { "engines/tts/qwen3-tts-$Profile-win-x64" }
$requirements = "runtime/requirements/$Server.$Profile.txt"

if ($Force -and (Test-Path "build/python-packs/$Server-$Profile")) {
  Remove-Item -Recurse -Force "build/python-packs/$Server-$Profile"
}

New-Item -ItemType Directory -Force -Path (Split-Path $venvDir -Parent) | Out-Null
if (!(Test-Path $venvDir)) {
  Write-Host "Creating venv $venvDir"
  Invoke-Expression "$Python -m venv `"$venvDir`""
}

$pythonExe = Join-Path $venvDir "Scripts/python.exe"
& $pythonExe -m pip install --upgrade pip setuptools wheel

if ($Server -eq "tts" -and $Profile -eq "nvidia-cu12") {
  Write-Host "Installing CUDA PyTorch into the TTS NVIDIA runtime pack..."
  & $pythonExe -m pip install --index-url https://download.pytorch.org/whl/cu126 torch torchvision torchaudio
}

Write-Host "Installing $requirements"
& $pythonExe -m pip install --no-cache-dir -r $requirements

# Optional user-controlled override for the exact Qwen backend, kept out of the committed file because it changes quickly.
if ($Server -eq "tts" -and $env:SIX_STARS_QWEN_TTS_BACKEND_REQUIREMENT) {
  Write-Host "Installing Qwen TTS backend override: $env:SIX_STARS_QWEN_TTS_BACKEND_REQUIREMENT"
  & $pythonExe -m pip install $env:SIX_STARS_QWEN_TTS_BACKEND_REQUIREMENT
}

$workDir = "build/python-packs/$Server-$Profile/pyinstaller"
New-Item -ItemType Directory -Force -Path $workDir | Out-Null

$pyiArgs = @(
  "-m", "PyInstaller",
  "--clean",
  "--noconfirm",
  "--onedir",
  "--name", $distName,
  "--paths", "scripts",
  "--distpath", "build/python-packs/$Server-$Profile/dist",
  "--workpath", $workDir,
  "--specpath", $workDir,
  "--hidden-import", "model_paths",
  $scriptPath
)

# NumPy 2.x and some PyInstaller hook combinations can produce a frozen exe that
# imports numpy._core but misses numpy._core._exceptions. Pinning ASR to NumPy
# 1.26.4 plus explicitly collecting NumPy keeps both ASR and TTS runtime packs
# from crashing before the server starts.
$pyiArgs += @(
  "--collect-all", "numpy",
  "--hidden-import", "numpy._core._exceptions",
  "--hidden-import", "numpy.core._exceptions"
)

if ($Server -eq "asr") {
  $pyiArgs += @(
    "--collect-all", "faster_whisper",
    "--collect-all", "ctranslate2",
    "--collect-all", "soundfile"
  )
}

if ($Server -eq "tts") {
  $pyiArgs += @(
    "--hidden-import", "torch",
    "--hidden-import", "websockets",
    "--collect-all", "soundfile"
  )
}

Write-Host "Running PyInstaller for $Server $Profile"
& $pythonExe @pyiArgs

if (Test-Path $outDir) { Remove-Item -Recurse -Force $outDir }
New-Item -ItemType Directory -Force -Path (Split-Path $outDir -Parent) | Out-Null
Copy-Item -Recurse "build/python-packs/$Server-$Profile/dist/$distName" $outDir

# Keep helper files/configs next to the exe for debugging and non-frozen fallback imports.
Copy-Item "scripts/model_paths.py" $outDir -Force
if ($Server -eq "tts") {
  Copy-Item "scripts/websocket_tts_server.py" $outDir -Force
} else {
  Copy-Item "scripts/faster_whisper_openai_server.py" $outDir -Force
}

@"
Generated Python runtime pack: $Server $Profile
Build script: scripts/Build-PythonRuntimePack.ps1
Do not merge this folder with the other profile. CPU/CUDA dependencies are intentionally isolated.
"@ | Set-Content (Join-Path $outDir "README.txt") -Encoding UTF8

Write-Host "Runtime pack copied to $outDir" -ForegroundColor Green

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
& $pythonExe -m pip install --upgrade pip
& $pythonExe -m pip install --force-reinstall "setuptools==80.9.0" "wheel==0.43.0"

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
  "--hidden-import", "numpy.core._exceptions"
)

if ($Server -eq "asr") {
    $pyiArgs += @(
        "--hidden-import", "numpy.core._exceptions",

        "--hidden-import", "pkg_resources",
        "--collect-submodules", "pkg_resources",
        "--copy-metadata", "setuptools",

        "--hidden-import", "requests",
        "--collect-submodules", "requests",

        "--hidden-import", "backports",
        "--hidden-import", "backports.tarfile",
        "--collect-submodules", "backports",
        "--copy-metadata", "backports.tarfile",

        "--collect-submodules", "jaraco",
        "--collect-all", "faster_whisper",
        "--collect-all", "ctranslate2",

        "--copy-metadata", "faster-whisper",
        "--copy-metadata", "ctranslate2"
    )
}

if ($Server -eq "tts") {
    $pyiArgs += @(
        "--debug", "noarchive",

        "--hidden-import", "backports",
        "--hidden-import", "backports.tarfile",
        "--collect-submodules", "backports",
        "--copy-metadata", "backports.tarfile",

        "--collect-submodules", "jaraco",
        "--collect-submodules", "jaraco.text",
        "--collect-submodules", "jaraco.context",

        "--collect-all", "transformers",
        "--collect-submodules", "transformers.models",
        "--collect-submodules", "transformers.generation",
        "--collect-submodules", "transformers.utils",
        "--copy-metadata", "transformers",

        "--collect-all", "tokenizers",
        "--copy-metadata", "tokenizers",

        "--collect-all", "accelerate",
        "--copy-metadata", "accelerate",

        "--collect-all", "safetensors",
        "--copy-metadata", "safetensors",

        "--collect-all", "sentencepiece",
        "--copy-metadata", "sentencepiece",

        "--collect-all", "huggingface_hub",
        "--copy-metadata", "huggingface-hub",

        "--collect-all", "qwen_tts",
        "--collect-all", "faster_qwen3_tts",

        "--collect-all", "torch",
        "--collect-all", "torchaudio",
        "--copy-metadata", "torch",
        "--copy-metadata", "torchaudio"
    )
}

Write-Host "Running PyInstaller for $Server $Profile"


if ($Server -eq "asr") {
    Write-Host "Checking ASR build imports..."
    & $pythonExe -c "import pkg_resources; import ctranslate2; from faster_whisper import WhisperModel; print('ASR imports OK')"

    if ($LASTEXITCODE -ne 0) {
        throw "ASR build environment is missing required imports. pkg_resources/ctranslate2/faster_whisper failed."
    }
}


if ($Server -eq "tts") {
    Write-Host "Checking TTS build imports before PyInstaller..."

    & $pythonExe -c "import torch; import transformers; import transformers.models; import tokenizers; import safetensors; import sentencepiece; print('TTS imports OK')"

    if ($LASTEXITCODE -ne 0) {
        throw "TTS build environment is missing required imports. torch/transformers/tokenizers/safetensors/sentencepiece failed."
    }
}

& $pythonExe @pyiArgs

if (Test-Path $outDir) { Remove-Item -Recurse -Force $outDir }
New-Item -ItemType Directory -Force -Path (Split-Path $outDir -Parent) | Out-Null
Copy-Item -Recurse "build/python-packs/$Server-$Profile/dist/$distName" $outDir

if ($Server -eq "asr" -and $Profile -eq "nvidia-cu12") {
    Write-Host "Copying NVIDIA CUDA/cuDNN runtime DLLs for faster-whisper..."

    $sitePackages = Join-Path $venvDir "Lib\site-packages"

    $nvidiaDlls = Get-ChildItem $sitePackages -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object {
            $_.Name -like "cudnn*.dll" -or
            $_.Name -like "cublas*.dll" -or
            $_.Name -like "cublasLt*.dll" -or
            $_.Name -like "cudart64_*.dll"
        }

    if (!$nvidiaDlls -or $nvidiaDlls.Count -eq 0) {
        throw "No NVIDIA cuDNN/cuBLAS DLLs were found in $sitePackages. Install nvidia-cudnn-cu12 and nvidia-cublas-cu12."
    }

    foreach ($dll in $nvidiaDlls) {
        Copy-Item $dll.FullName -Destination $outDir -Force
    }

    Write-Host "Copied $($nvidiaDlls.Count) NVIDIA runtime DLLs to:"
    Write-Host "  $outDir"
}

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

if ($Server -eq "tts") {
    $expectedTransformersModels = Join-Path $outDir "_internal\transformers\models\__init__.pyc"

    if (!(Test-Path $expectedTransformersModels)) {
        Write-Warning "Expected transformers models file was not found:"
        Write-Warning "  $expectedTransformersModels"
        Write-Warning "TTS may fail unless PyInstaller noarchive mode is enabled."
    }
    else {
        Write-Host "Verified transformers.models exists:"
        Write-Host "  $expectedTransformersModels"
    }
}

Write-Host "Runtime pack copied to $outDir" -ForegroundColor Green

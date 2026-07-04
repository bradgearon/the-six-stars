@echo off
setlocal EnableExtensions
cd /d "%~dp0\.."

set "NO_PAUSE=false"
if /I "%~1"=="/nopause" set "NO_PAUSE=true"

if not exist ".venv\Scripts\activate.cmd" (
  if exist ".venv\Scripts\activate.bat" (
    call ".venv\Scripts\activate.bat"
  ) else (
    echo .venv was not found. Run setup.cmd first, or create it with: py -3.11 -m venv .venv
    if /I not "%NO_PAUSE%"=="true" pause
    exit /b 1
  )
) else (
  call ".venv\Scripts\activate.cmd"
)

if not defined TORCH_CUDA_INDEX set "TORCH_CUDA_INDEX=cu126"
set "TORCH_INDEX_URL=https://download.pytorch.org/whl/%TORCH_CUDA_INDEX%"

echo ======================================================================
echo Installing CUDA PyTorch for Six Stars voice/TTS
echo Repo: %CD%
echo CUDA wheel index: %TORCH_INDEX_URL%
echo ======================================================================
echo.

echo Checking NVIDIA driver...
nvidia-smi >nul 2>nul
if errorlevel 1 (
  echo WARNING: nvidia-smi did not run. If this machine has an NVIDIA GPU, install/update the NVIDIA driver first.
  echo The CUDA PyTorch wheel can still install, but torch.cuda.is_available may stay false until the driver works.
) else (
  nvidia-smi
)

echo.
echo Removing any CPU-only or broken PyTorch packages...
python -m pip uninstall -y torch torchvision torchaudio

echo.
echo Installing CUDA PyTorch wheels...
if /I "%TORCH_CUDA_INDEX%"=="cu126" (
  python -m pip install --no-cache-dir -r requirements-torch-cuda-cu126.txt
) else (
  python -m pip install --no-cache-dir torch torchvision torchaudio --index-url "%TORCH_INDEX_URL%"
)
if errorlevel 1 (
  echo.
  echo ERROR: CUDA PyTorch install failed from %TORCH_INDEX_URL%.
  echo Try a newer wheel index:
  echo   set TORCH_CUDA_INDEX=cu128
  echo   scripts\install_cuda_torch.cmd
  if /I not "%NO_PAUSE%"=="true" pause
  exit /b 1
)

echo.
echo Installing Qwen voice packages after CUDA torch so pip does not pull CPU torch...
python -m pip install --no-cache-dir --upgrade qwen-tts faster-qwen3-tts
if errorlevel 1 (
  echo WARNING: qwen-tts or faster-qwen3-tts install failed. The WebSocket TTS server may not start.
)

echo.
echo Re-pinning transformer/audio helper packages for the Qwen3-TTS compatibility path...
python -m pip install --no-cache-dir --upgrade --force-reinstall "transformers==4.57.3" "huggingface-hub>=0.24.0" "accelerate>=0.34.0" "safetensors>=0.4.0" "sentencepiece>=0.2.0" "protobuf>=4.25.0" packaging websockets numpy scipy soundfile
if errorlevel 1 (
  echo ERROR: dependency repair failed.
  if /I not "%NO_PAUSE%"=="true" pause
  exit /b 1
)

echo.
echo Verifying CUDA PyTorch...
python scripts\verify_torch_cuda.py
if errorlevel 1 (
  echo.
  echo ERROR: PyTorch installed, but CUDA is not available to this venv.
  echo If you have an NVIDIA GPU, check nvidia-smi and install a current NVIDIA driver.
  echo You can test slowly with CPU by setting QWEN_TTS_DEVICE=cpu, but Qwen voice will be much slower.
  if /I not "%NO_PAUSE%"=="true" pause
  exit /b 1
)

echo.
echo Verifying Qwen imports...
python -c "import transformers; print('Transformers:', transformers.__version__); import qwen_tts; print('qwen_tts import: OK')"
if errorlevel 1 (
  echo WARNING: qwen_tts import failed. Check the install output above.
)

echo.
echo CUDA voice stack is ready.
if /I not "%NO_PAUSE%"=="true" pause
exit /b 0

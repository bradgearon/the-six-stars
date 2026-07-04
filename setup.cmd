@echo off
setlocal EnableExtensions
cd /d "%~dp0"

echo ======================================================================
echo Six Stars one-time Python setup for Whisper + Qwen voice
echo LM Studio remains the default LLM. This only installs local Python services.
echo ======================================================================
echo.

if exist ".venv\Scripts\activate.cmd" goto activate_env
if exist ".venv\Scripts\activate.bat" goto activate_env

echo Creating .venv...
py -3.11 -m venv .venv
if errorlevel 1 (
  echo py -3.11 failed. Trying py -3.12...
  py -3.12 -m venv .venv
)
if errorlevel 1 (
  echo py -3.12 failed. Trying python -m venv...
  python -m venv .venv
)
if errorlevel 1 (
  echo ERROR: failed to create .venv. Install Python 3.11 or 3.12 and try again.
  pause
  exit /b 1
)

:activate_env
if exist ".venv\Scripts\activate.cmd" (
  call ".venv\Scripts\activate.cmd"
) else (
  call ".venv\Scripts\activate.bat"
)
if errorlevel 1 exit /b 1

set "PYTHONNOUSERSITE=1"
if not defined SIX_STARS_MODELS_ROOT set "SIX_STARS_MODELS_ROOT=%CD%\models"
if not defined HF_HOME set "HF_HOME=%CD%\models\hf"
if not defined HF_HUB_CACHE set "HF_HUB_CACHE=%CD%\models\hf\hub"

echo.
echo Upgrading pip tooling...
python -m pip install --upgrade pip setuptools wheel
if errorlevel 1 exit /b 1

echo.
echo Installing requirements, including CUDA PyTorch from the PyTorch cu126 wheel index...
python -m pip install --no-cache-dir -r requirements.txt
if errorlevel 1 exit /b 1

echo.
echo Verifying CUDA PyTorch...
python scripts\verify_torch_cuda.py
if errorlevel 1 (
  echo.
  echo CUDA verification failed after requirements.txt. Running the repair installer...
  call scripts\install_cuda_torch.cmd /nopause
  if errorlevel 1 exit /b 1
)

echo.
echo Setup complete. Start LM Studio and load a model, then run:
echo   launch.cmd
echo.
pause
exit /b 0

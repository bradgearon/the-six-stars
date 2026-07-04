@echo off
setlocal EnableExtensions
cd /d "%~dp0"

if not exist ".venv\Scripts\activate.cmd" (
  if exist ".venv\Scripts\activate.bat" (
    call ".venv\Scripts\activate.bat"
  ) else (
    echo .venv was not found.
    echo Run setup.cmd once first, or create it manually with: py -3.11 -m venv .venv
    exit /b 1
  )
) else (
  call ".venv\Scripts\activate.cmd"
)

set "PYTHONNOUSERSITE=1"
if not defined SIX_STARS_MODELS_ROOT set "SIX_STARS_MODELS_ROOT=%CD%\models"
if not defined HF_HOME set "HF_HOME=%CD%\models\hf"
if not defined HF_HUB_CACHE set "HF_HUB_CACHE=%CD%\models\hf\hub"

rem Use LM Studio as the default LLM. Start a model in LM Studio first.
if not defined SIX_STARS_START_LOCAL_LLM set "SIX_STARS_START_LOCAL_LLM=false"
if not defined SIX_STARS_LLM_ENDPOINT set "SIX_STARS_LLM_ENDPOINT=http://localhost:8080/v1/chat/completions"
if not defined SIX_STARS_LLM_MODEL set "SIX_STARS_LLM_MODEL=local-model"
rem LM Studio Developer Server may require an API token. Set LM_API_TOKEN before launch,
rem or set SIX_STARS_LLM_API_KEY directly. The WPF app also reads both names.
if not defined SIX_STARS_LLM_API_KEY if defined LM_API_TOKEN set "SIX_STARS_LLM_API_KEY=%LM_API_TOKEN%"
if not defined SIX_STARS_SCREEN_LLM_API_KEY if defined SIX_STARS_LLM_API_KEY set "SIX_STARS_SCREEN_LLM_API_KEY=%SIX_STARS_LLM_API_KEY%"

rem Qwen voice is fast only with CUDA PyTorch. We do a quick preflight so CPU-only
rem torch fails with a clear setup instruction instead of a late TTS crash.
if not defined QWEN_TTS_DEVICE set "QWEN_TTS_DEVICE=cuda"
if /I not "%QWEN_TTS_DEVICE%"=="cpu" if /I not "%SIX_STARS_SKIP_TORCH_CHECK%"=="true" (
  python scripts\verify_torch_cuda.py --quiet
  if errorlevel 1 (
    echo.
    echo WARNING: CUDA PyTorch is not ready in this .venv.
    echo Voice generation will fail or be very slow until you run:
    echo   setup.cmd
    echo or:
    echo   scripts\install_cuda_torch.cmd
    echo.
    echo To try the slow CPU path instead, run:
    echo   set QWEN_TTS_DEVICE=cpu
    echo   launch.cmd
    echo.
  )
)

python scripts\start_services.py

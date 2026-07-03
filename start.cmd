@echo off
setlocal EnableExtensions
cd /d "%~dp0"

echo ======================================================================
echo Six Stars backend launcher
echo Repo: %CD%
echo ======================================================================

if not exist ".venv\Scripts\activate.bat" (
  echo No .venv found. Creating local Python virtual environment...
  py -3.12 -m venv .venv
  if errorlevel 1 (
    echo py -3.12 failed. Trying python -m venv .venv ...
    python -m venv .venv
    if errorlevel 1 (
      echo Failed to create .venv. Install Python 3.12 and try again.
      exit /b 1
    )
  )
)

echo Activating .venv...
call ".venv\Scripts\activate.bat"
if errorlevel 1 (
  echo Failed to activate .venv\Scripts\activate.bat
  exit /b 1
)

set "PYTHONNOUSERSITE=1"
if not defined QWEN_TTS_DEVICE set "QWEN_TTS_DEVICE=cuda"
if not defined QWEN_TTS_LANGUAGE set "QWEN_TTS_LANGUAGE=English"
if not defined QWEN_TTS_MODEL_PATH set "QWEN_TTS_MODEL_PATH=Qwen/Qwen3-TTS-12Hz-0.6B-Base"
if not defined QWEN_TTS_CLONE_AUDIO_DIR set "QWEN_TTS_CLONE_AUDIO_DIR=src\Wds.SixStars.Api\Content\VoiceClones"
if not defined QWEN_TTS_STARS_DIR set "QWEN_TTS_STARS_DIR=src\Wds.SixStars.Api\Content\Stars"
if not defined QWEN_TTS_CHUNK_GENERATION_MODE set "QWEN_TTS_CHUNK_GENERATION_MODE=first_then_prefetch"
if not defined QWEN_TTS_PARALLEL_CHUNKS set "QWEN_TTS_PARALLEL_CHUNKS=false"
if not defined QWEN_TTS_MAX_PARALLEL_CHUNKS set "QWEN_TTS_MAX_PARALLEL_CHUNKS=1"
if not defined QWEN_TTS_USE_CUDA_GRAPHS set "QWEN_TTS_USE_CUDA_GRAPHS=true"
if not defined QWEN_TTS_USE_FASTER_QWEN3 set "QWEN_TTS_USE_FASTER_QWEN3=true"
if not defined QWEN_TTS_CUDA_GRAPH_WARMUP_RUNS set "QWEN_TTS_CUDA_GRAPH_WARMUP_RUNS=3"
if not defined SIX_STARS_START_OPENWAKEWORD set "SIX_STARS_START_OPENWAKEWORD=false"
if not defined SIX_STARS_WHISPER_ENDPOINT set "SIX_STARS_WHISPER_ENDPOINT=http://localhost:8000/v1/audio/transcriptions"
if not defined SIX_STARS_REQUIRE_WAKE_WORD set "SIX_STARS_REQUIRE_WAKE_WORD=false"
if not defined SIX_STARS_START_WHISPER set "SIX_STARS_START_WHISPER=true"
if not defined WHISPER_PRELOAD set "WHISPER_PRELOAD=true"
if not defined WHISPER_MODEL set "WHISPER_MODEL=tiny.en"

if /I not "%SIX_STARS_SKIP_PIP_INSTALL%"=="true" (
  if exist "requirements.txt" (
    if not exist ".venv\.sixstars_requirements_installed" (
      echo Installing Python requirements into .venv. Set SIX_STARS_SKIP_PIP_INSTALL=true to skip this.
      python -m pip install --upgrade pip setuptools wheel
      if errorlevel 1 exit /b 1
      python -m pip install -r requirements.txt
      if errorlevel 1 exit /b 1
      echo installed>".venv\.sixstars_requirements_installed"
    )
  )
  python -c "import faster_qwen3_tts" 2>nul
  if errorlevel 1 (
    echo Installing faster-qwen3-tts for Qwen3 CUDA graph acceleration...
    python -m pip install faster-qwen3-tts
    if errorlevel 1 (
      echo WARNING: faster-qwen3-tts install failed. The TTS server will fall back to slower qwen_tts.
    )
  )
)

echo.
echo Python executable:
python -c "import sys; print(sys.executable)"
echo.
echo Starting managed backend services...
python scripts\start_services.py

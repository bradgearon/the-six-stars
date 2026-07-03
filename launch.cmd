@echo off
setlocal EnableExtensions
cd /d "%~dp0"

if not exist ".venv\Scripts\activate.cmd" (
  if exist ".venv\Scripts\activate.bat" (
    call ".venv\Scripts\activate.bat"
  ) else (
    echo .venv was not found. Create it first with: python -m venv .venv
    exit /b 1
  )
) else (
  call ".venv\Scripts\activate.cmd"
)

rem Use LM Studio as the default LLM. Start a model in LM Studio first.
if not defined SIX_STARS_START_LOCAL_LLM set "SIX_STARS_START_LOCAL_LLM=false"
if not defined SIX_STARS_LLM_ENDPOINT set "SIX_STARS_LLM_ENDPOINT=http://localhost:8080/v1/chat/completions"
if not defined SIX_STARS_LLM_MODEL set "SIX_STARS_LLM_MODEL=local-model"

python scripts\start_services.py

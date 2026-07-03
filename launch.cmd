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

python scripts\start_services.py

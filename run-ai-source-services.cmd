@echo off
setlocal EnableExtensions
cd /d "%~dp0"

rem Source/venv services launcher for debugging Python ASR/TTS.
rem This uses the repo .venv for Python services and the llama.cpp engine pack
rem for the LLM by default. You can also run this manually after activating .venv:
rem     python scripts\start_services.py
set "SIX_STARS_USE_ENGINE_PACKS=llm"
call start.cmd %*

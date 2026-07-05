# Venv Source Services Launcher Fix

This update changes the services-only launch path back to the local Python virtual environment for the Python sidecars.

## Behavior

- `run-ai-companion.cmd` now calls `start.cmd` directly.
- `start.cmd` activates/creates `.venv` and runs `python scripts\start_services.py` from that venv.
- `scripts/start_services.py` now defaults to `SIX_STARS_USE_ENGINE_PACKS=llm` behavior:
  - LLM uses the llama.cpp engine pack because that server is not Python.
  - Whisper/ASR uses `.venv` + `scripts/faster_whisper_openai_server.py`.
  - Qwen TTS uses `.venv` + `scripts/websocket_tts_server.py`.
- `run-ai-engine-services.cmd` was added for testing the frozen engine EXE build for all services.

## Overrides

```bat
rem Default/debug-friendly behavior: llama.cpp LLM engine + source Python ASR/TTS
set SIX_STARS_USE_ENGINE_PACKS=llm
run-ai-companion.cmd

rem Fully frozen redist engine behavior
set SIX_STARS_USE_ENGINE_PACKS=true
run-ai-companion.cmd

rem Source Python fallback for everything possible
set SIX_STARS_USE_ENGINE_PACKS=false
run-ai-companion.cmd
```

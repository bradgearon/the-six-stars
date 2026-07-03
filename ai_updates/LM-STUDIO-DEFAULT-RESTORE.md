# LM Studio Default Restore

The Python Transformers Gemma server was too slow for the current desktop/story
workflow, so the default LLM path has been switched back to LM Studio.

## Defaults restored

- `LLM.EndpointUrl`: `http://localhost:8080/v1/chat/completions`
- `LLM.Model`: `local-model`
- `ScreenLLM.EndpointUrl`: `http://localhost:8080/v1/chat/completions`
- `ScreenLLM.Model`: `local-model`
- `LocalModels.LlmEndpointUrl`: `http://localhost:8080/v1/chat/completions`
- `LocalModels.LlmModel`: `local-model`
- `SIX_STARS_START_LOCAL_LLM`: default `false`

## Launcher behavior

`scripts/start_services.py` now starts Whisper and WebSocket Qwen TTS by default,
but does not start the Python Gemma LLM unless explicitly requested.

It prints/checks the external LM Studio `/v1/models` endpoint as a warning only.
The launcher no longer waits on the Python LLM health endpoint unless
`SIX_STARS_START_LOCAL_LLM=true`.

## Optional Python Gemma server

The Python Gemma service remains in the repo for future testing:

```bat
set SIX_STARS_START_LOCAL_LLM=true
set LOCAL_LLM_MODEL=google/gemma-4-E2B-it
call .venv\scripts\activate.cmd
python scripts\start_services.py
```

But the recommended current path is LM Studio for speed.

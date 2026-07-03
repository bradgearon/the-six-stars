# Local LLM E2B Ready/Health Fix

This update fixes the local Gemma service setup after the local model service layer update.

## Changes

- Changed the default local LLM model from `google/gemma-4-E4B-it` to `google/gemma-4-E2B-it` everywhere relevant:
  - `scripts/local_llm_openai_server.py`
  - `scripts/start_services.py`
  - `src/Wds.SixStars.Ui/appsettings.json`
  - `src/Wds.SixStars.Ui/Configuration/AppConfiguration.cs`
  - `src/Wds.SixStars.Api/LocalModels/LocalModelServiceOptions.cs`
  - `appsettings.sixstars.example.json`
  - `models/README.txt`
  - `start.cmd`

- Added friendly model aliases for the local LLM server and launcher:
  - `e2b`
  - `gemma-e2b`
  - `gemma-2b`
  - `gemma-4-e2b`
  - `gemma-4-e2b-it`

- Changed local LLM preload so the Flask server starts immediately and preloads the model in a background thread.
  - `/health` is now available while the model downloads/loads.
  - The launcher no longer reports connection-refused just because preload is still happening.

- Added local LLM health status fields:
  - `status`: `ready`, `loading`, `error`, or `not_loaded`
  - `ready`
  - `loaded`
  - `loading`
  - `lastLoadError`
  - `resolvedModel`

- Updated `scripts/start_services.py` to wait for `/health` before probing `/v1/models`.
  - It waits up to `LOCAL_LLM_READY_WAIT_SECONDS` seconds, default `180`.
  - If still loading, the launcher keeps running and tells you to watch `logs/services/local_gemma_llm.log`.

- Removed the hard Gemma 4 Transformers version failure by default.
  - The server now warns if Transformers may be old, but still tries `AutoProcessor` and slow-tokenizer fallback.
  - Set `LOCAL_LLM_STRICT_GEMMA4_VERSION_CHECK=true` to restore the hard version check.

- Changed requirements from `transformers>=5.5.0` to `transformers>=4.53.0` so pip will not fail on an unavailable strict version while still allowing upgrades.

## Recommended launch

```bat
call .venv\scripts\activate.cmd
python scripts\start_services.py
```

Optional override:

```bat
set LOCAL_LLM_MODEL=gemma-e2b
```

This resolves to:

```text
google/gemma-4-E2B-it
```

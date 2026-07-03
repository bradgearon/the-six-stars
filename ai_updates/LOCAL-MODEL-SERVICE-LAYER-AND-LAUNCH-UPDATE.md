# Local model service layer and launch update

This update creates one shared local AI service layer for the desktop harness and future Unity usage.

## Added

- `scripts/model_paths.py`
  - Centralizes `models/` root handling.
  - Sets `SIX_STARS_MODELS_ROOT`, `HF_HOME`, and `HF_HUB_CACHE`.
  - Resolves local model folders before falling back to Hugging Face model ids.

- `scripts/local_llm_openai_server.py`
  - OpenAI-compatible local LLM server.
  - Endpoints:
    - `GET /health`
    - `GET /v1/models`
    - `POST /v1/chat/completions`
  - Uses Hugging Face Transformers and can load Gemma or another causal chat/instruct model.

- API reusable local model layer:
  - `Wds.SixStars.Api/LocalModels/LocalModelServiceOptions.cs`
  - `Wds.SixStars.Api/LocalModels/LocalModelPaths.cs`
  - `Wds.SixStars.Api/LocalModels/LocalAiClient.cs`

- `launch.cmd`
  - Simple launcher matching the user's working flow:
    - activate `.venv`
    - run `python scripts\start_services.py`

## Changed

- `scripts/start_services.py`
  - Now sets repository-local model cache paths.
  - Starts the local LLM server on port `8200` by default.
  - Starts faster-whisper with `models/asr` as its download root.
  - Starts Qwen TTS with local `models/tts` aliases if present.

- `scripts/faster_whisper_openai_server.py`
  - Uses shared model path resolution.
  - Downloads/loads from `models/asr` by default.

- `scripts/websocket_tts_server.py`
  - Uses shared model path resolution.
  - Checks `models/tts` for local Qwen folders before Hugging Face download.

- `appsettings.json`
  - LLM endpoint now defaults to `http://localhost:8200/v1/chat/completions`.
  - Model defaults to `google/gemma-4-E4B-it`.
  - Added a `LocalModels` section.

## Fixed

- Story generation after the quick thinking phrase is now detached from the short foreground TTS operation token, so starting the placeholder voice no longer cancels the long background story LLM call.

- DirectSound playback now decodes WAV data into a stable PCM buffer before passing it to `DirectSoundOut`, avoiding the fragile WaveFileReader provider path that was throwing `Object contains references. (Parameter 'value')` on some systems.

## Model folder

Use:

```text
models/
  hf/hub/
  llm/
  tts/
  asr/
```

Optional local folders:

```text
models/llm/gemma-4-E4B-it/
models/tts/qwen3-tts-0.6b/
models/asr/faster-whisper-tiny.en/
```

# Python local path + engine services launcher fix

This update keeps the services-only launcher aligned with the newer engine/runtime-pack build.

## Fixed

- `scripts/model_paths.py` now finds the real repository/app root when running from:
  - source scripts,
  - frozen Python EXEs under `engines/...`,
  - a manually launched engine folder,
  - or a runtime pack with `SIX_STARS_APP_ROOT` / `SIX_STARS_REPO_ROOT` set.
- Local paths like `models/tts/qwen3-tts-0.6b-base` now resolve against the app root, not the current engine folder.
- The .NET launcher now passes `SIX_STARS_APP_ROOT` and `SIX_STARS_REPO_ROOT` to every runtime pack.
- `scripts/start_services.py` now prefers `config/runtime-manifest.json` engine packs:
  - LLM: `engines/llm/.../llama-server.exe`
  - ASR: `engines/asr/.../faster_whisper_openai_server.exe`
  - TTS: `engines/tts/.../websocket_tts_server.exe`
- `start.cmd` now defaults to starting the local LLM service on port `8080`, matching the manifest and WPF defaults.

## Notes

`run-ai-companion.cmd` uses the .NET launcher and is still the preferred path. `start.cmd` / `scripts/start_services.py` are now usable again as source-level services-only launchers when testing manually.

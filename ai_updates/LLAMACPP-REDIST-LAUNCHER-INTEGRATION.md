# llama.cpp Redist Launcher Integration

Implemented a new integrated runtime path for the Six Stars companion app:

- Added `Wds.SixStars.Launcher`, a .NET launcher exe that can run setup, start local AI servers, and launch the WPF UI.
- Added `config/runtime-manifest.json` and `config/models.json` for runtime pack selection and model selection.
- Added llama.cpp dynamic build script with `GGML_BACKEND_DL=ON` support.
- Added isolated PyInstaller onedir runtime pack builds for faster-whisper and Qwen3/faster-qwen TTS.
- Added redist build script to publish the UI, publish the launcher, copy runtime packs, and create `redist/six-stars-ai-companion-redist.zip`.
- Updated `launch.cmd` and `setup.cmd` so the default path is now the integrated launcher instead of LM Studio.

Default endpoints used by the WPF app:

```text
LLM:     http://127.0.0.1:8080/v1/chat/completions
ASR:     http://127.0.0.1:8000/v1/audio/transcriptions
TTS:     ws://127.0.0.1:8100/tts/stream
```

The launcher injects `-m models/llm/gemma-e4b-it.gguf` when a local GGUF exists; otherwise it uses the configured llama.cpp `-hf` specifier.

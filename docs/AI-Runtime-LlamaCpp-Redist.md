# Six Stars AI Runtime: llama.cpp + faster-whisper + Qwen3-TTS

This update integrates the local AI stack into the companion app build instead of depending on LM Studio.

## Runtime stack

```text
six-stars-launcher.exe
  ├─ llama.cpp dynamic llama-server on http://127.0.0.1:8080/v1/chat/completions
  ├─ faster-whisper OpenAI-compatible ASR server on http://127.0.0.1:8000/v1/audio/transcriptions
  ├─ Qwen3/faster-qwen WebSocket TTS server on ws://127.0.0.1:8100/tts/stream
  └─ WPF companion UI: six-stars.exe
```

The WPF app already uses OpenAI-compatible chat requests, so the LLM request now goes to llama.cpp's server endpoint. The launcher sets these variables before opening the UI:

```text
SIX_STARS_LLM_ENDPOINT=http://127.0.0.1:8080/v1/chat/completions
SIX_STARS_LLM_MODEL=local-model
SIX_STARS_SCREEN_LLM_ENDPOINT=http://127.0.0.1:8080/v1/chat/completions
SIX_STARS_WHISPER_ENDPOINT=http://127.0.0.1:8000/v1/audio/transcriptions
SIX_STARS_WEBSOCKET_TTS_URL=ws://127.0.0.1:8100/tts/stream
```

## First run/build commands

From the repo root:

```cmd
setup.cmd
launch.cmd
```

To build a redistributable folder and launcher exe:

```cmd
build-ai-redist.cmd -Profile auto
```

The final runtime folder is `redist/`, with a `six-stars-launcher.exe` and `Launch Six Stars AI Companion.cmd`.

## Build scripts

| Script | Purpose |
|---|---|
| `scripts/Install-AiRuntime.ps1` | Main installer/build coordinator. Detects NVIDIA when `-Profile auto` is used. |
| `scripts/Build-LlamaCppDynamic.ps1` | Clones/builds llama.cpp with `GGML_BACKEND_DL=ON`, shared libraries, Vulkan, and optional CUDA. |
| `scripts/Build-PythonRuntimePack.ps1` | Builds isolated PyInstaller `--onedir` packs for ASR and TTS. |
| `scripts/Download-AiModels.ps1` | Prepares model folders and downloads Qwen TTS when configured. llama.cpp can download GGUF via `-hf`. |
| `scripts/Build-AiRuntimeRedist.ps1` | Publishes UI + launcher and copies config/runtime packs into `redist/`. |
| `scripts/Run-AiCompanionLauncher.ps1` | Runs the launcher from source using `dotnet run`. |

## Runtime packs

```text
engines/
  llm/llama-cpp-dynamic-win-x64/llama-server.exe
  asr/faster-whisper-cpu-win-x64/faster_whisper_openai_server.exe
  asr/faster-whisper-nvidia-cu12-win-x64/faster_whisper_openai_server.exe
  tts/qwen3-tts-cpu-win-x64/websocket_tts_server.exe
  tts/qwen3-tts-nvidia-cu12-win-x64/websocket_tts_server.exe
```

CPU and NVIDIA Python packs are intentionally isolated. Do not merge them. This avoids PyTorch/CUDA/CTranslate2 DLL conflicts.

## Model config

`config/models.json` controls the default local model set:

```json
{
  "llm": {
    "id": "gemma-e4b-it-gguf",
    "preferredMode": "llama-hf",
    "hfSpecifier": "unsloth/gemma-3n-E4B-it-GGUF:UD-Q4_K_XL",
    "localPath": "models/llm/gemma-e4b-it.gguf"
  }
}
```

If `models/llm/gemma-e4b-it.gguf` exists, the launcher starts llama.cpp with `-m`. Otherwise it starts llama.cpp with `-hf` using the configured Hugging Face specifier.

## Qwen3-TTS backend note

The committed TTS server already contains the current Qwen/faster-qwen adapter logic. Because the Qwen3-TTS Python ecosystem changes quickly, the runtime pack script supports this override before building the TTS pack:

```powershell
$env:SIX_STARS_QWEN_TTS_BACKEND_REQUIREMENT = "faster-qwen3-tts @ git+https://github.com/andimarafioti/faster-qwen3-tts.git"
./scripts/Build-PythonRuntimePack.ps1 -Server tts -Profile nvidia-cu12 -Force
```

Use the exact package/version that is working in your dev environment.

## Activation

The redistributable core is hotkey/UI button first. Porcupine should stay optional unless you have redistribution rights for the wake-word runtime and model. The app can still use a button combination or UI buttons without any wake-word dependency.

# Six Stars Native Runtime Quick Start

The default local runtime is native. **Python, PyTorch, CTranslate2, and a
virtual environment are not required to run the companion.**

The launcher owns the process tree and starts/stops three native inference
services:

1. **llama-server** on `127.0.0.1:8080` for the OpenAI-compatible LLM API.
2. **whisper.cpp** (`whisper-server.exe`) on `127.0.0.1:8000` for ASR.
3. **qwentts.cpp** (`tts-server.exe`) on `127.0.0.1:8101` for Qwen3-TTS.

The WPF/.NET application talks directly to all three HTTP services. There is no
Drogon/native WebSocket gateway. The existing .NET speech pipeline remains the
orchestration layer: it registers Star clone voices with qwentts.cpp, submits
short TTS chunks, receives WAVs, and feeds the existing ordered playback/mobile
bridge pipeline.

Closing the desktop UI causes `Wds.SixStars.Launcher` to terminate the managed
native service processes.

## End-user prerequisites

For the prebuilt Windows redistribution:

- Windows x64.
- A compatible NVIDIA GPU and **NVIDIA display driver** (`nvidia-smi` must work).
- The redistributed engine folders and model files.

A full CUDA Toolkit installation is **not inherently required**. If the
whisper.cpp/qwentts.cpp/llama.cpp CUDA builds dynamically use CUDA runtime
libraries, ship the redistributable CUDA DLLs beside the executables. In that
packaging model the machine only needs the compatible NVIDIA driver.

The release build publishes the .NET launcher/UI self-contained, so the end-user
redistribution does not need a separate .NET Desktop Runtime install.

## Runtime folder layout

```text
engines/
  llm/llama-cpp-dynamic-win-x64/
    llama-server.exe
    ...llama.cpp CUDA/runtime DLLs...
  asr/whispercpp-nvidia-win-x64/
    whisper-server.exe
    ...ggml/CUDA runtime DLLs used by your build...
  tts/qwenttscpp-nvidia-win-x64/
    tts-server.exe
    ...qwentts/ggml/CUDA runtime DLLs used by your build...

models/
  llm/gemma-4-E4B-it-Q6_K.gguf
  llm/mmproj-gemma-4-E4B-it-Q8_0.gguf       (optional vision projector)
  asr/ggml-large-v3-turbo.bin
  tts/qwen-talker-0.6b-base-Q8_0.gguf
  tts/qwen-tokenizer-12hz-Q8_0.gguf
```

The exact locations are controlled by `config/runtime-manifest.json` and
`config/models.json`.

## Development setup

Put the complete prebuilt native folders under `runtime-packs/win-x64/` using
the names above, then run:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/Install-AiRuntime.ps1
```

The installer copies/validates the three native packs. It does not construct a
Python runtime.

Build the solution normally:

```powershell
dotnet build SixStars.sln
```

Then launch everything with:

```cmd
launch.cmd
```

or run only the three native inference services without the WPF UI:

```cmd
launch.cmd --no-ui
```

## llama-server model loading

The launcher passes `models.llm.localPath` directly to llama-server with `-m`.
There is no Ollama daemon, model registry, or `ollama create` step. The .NET
client uses `http://127.0.0.1:8080/v1/chat/completions`.

When `models.vision.enabled` is true and the configured mmproj exists, the
launcher also appends `--mmproj`.

## whisper.cpp

The launcher supplies the configured model path and starts approximately:

```text
whisper-server.exe -m models/asr/ggml-large-v3-turbo.bin \
  --host 127.0.0.1 --port 8000 --language en --no-context --flash-attn
```

The C# adapter posts microphone WAV data directly to whisper.cpp's native
`/inference` route and requests JSON output.

## qwentts.cpp direct from .NET

The launcher keeps qwentts.cpp hot on the GPU:

```text
tts-server.exe \
  --model models/tts/qwen-talker-0.6b-base-Q8_0.gguf \
  --codec models/tts/qwen-tokenizer-12hz-Q8_0.gguf \
  --alias qwen3-tts-base --host 127.0.0.1 --port 8101
```

`QwenCppHttpStreamingVoiceGenerator` in `Wds.SixStars.Api` owns the local TTS
protocol. For a Star clone it registers the reference WAV once with:

```text
POST http://127.0.0.1:8101/v1/audio/voices
```

and then sends each intentional `spokenText`/`TextParts` item directly to:

```text
POST http://127.0.0.1:8101/v1/audio/speech
```

using `response_format=wav`. Each returned WAV is raised through the existing
`IStreamingVoiceGenerator.AudioChunkReady` event, so the current .NET ordered
playback/mobile delivery code remains in place while the next chunk generates.

## Build a redistribution

Once `engines/` and `models/` contain the distributable native files:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/Build-AiRuntimeRedist.ps1 -SkipRuntimeInstall
```

The output is:

`redist/six-stars-ai-companion-native-redist.zip`

## Legacy Python services

The older Python scripts remain in `scripts/` for benchmarking/regression
comparison, but they are not part of the default native launcher/runtime path.

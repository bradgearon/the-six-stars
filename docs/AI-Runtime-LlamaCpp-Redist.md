# Six Stars AI Runtime: llama.cpp + whisper.cpp + qwentts.cpp

The packaged local runtime keeps the existing llama.cpp `llama-server` for the
LLM and replaces the Python ASR/TTS processes with `whisper.cpp` and
`qwentts.cpp`. Networking/orchestration stays in .NET; no additional native
HTTP/WebSocket gateway is required.

## Runtime stack

```text
six-stars-launcher.exe
  ├─ llama-server.exe       http://127.0.0.1:8080/v1/chat/completions
  ├─ whisper-server.exe     http://127.0.0.1:8000/inference
  ├─ tts-server.exe         http://127.0.0.1:8101
  └─ WPF/.NET companion     calls all three services directly
```

`Wds.SixStars.Launcher` owns the three native process lifetimes, keeps the
engines hot, waits for readiness, forwards endpoint/model environment settings
to the UI, and stops the local services when the UI exits.

## LLM

The launcher starts the redistributed `llama-server.exe` directly. If
`models.llm.localPath` exists it passes that GGUF with `-m`; otherwise the
existing `hfSpecifier` fallback remains available. When the optional vision
projector exists and vision is enabled, the launcher appends `--mmproj`.

There is no Ollama daemon, model registry, model import, or Ollama model store in
this path.

## ASR

The launcher starts native `whisper-server.exe` and passes the configured
Whisper model with `-m`. The C# speech adapter posts microphone WAV data directly
to `/inference`.

## TTS

The launcher starts native qwentts.cpp `tts-server.exe` with the configured
Talker and codec GGUFs. `QwenCppHttpStreamingVoiceGenerator` in the .NET API
registers clone voices through `/v1/audio/voices` and submits each existing
Six Stars speech chunk to `/v1/audio/speech`. WAV results are raised through the
existing `IStreamingVoiceGenerator` events, so playback, cancellation, story
prefetch, and Android/mobile delivery stay in the .NET application.

## Runtime packs

```text
engines/
  llm/llama-cpp-dynamic-win-x64/llama-server.exe
  asr/whispercpp-nvidia-win-x64/whisper-server.exe
  tts/qwenttscpp-nvidia-win-x64/tts-server.exe
```

Stage the complete prebuilt folders, including the backend/CUDA runtime DLLs
required by each exact build.

## Installation requirement

For the NVIDIA-only packaged profile, the end-user machine needs a compatible
NVIDIA GPU/display driver (`nvidia-smi` must work). The full CUDA Toolkit is
normally a build-time dependency, not an end-user dependency, provided the
redistribution includes the CUDA runtime/backend DLLs required by the prebuilt
binaries.

The production path does not need Python, pip, a virtual environment, PyTorch,
Transformers, CTranslate2, Drogon, or Trantor.

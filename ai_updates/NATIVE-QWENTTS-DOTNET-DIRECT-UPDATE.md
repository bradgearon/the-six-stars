# Native qwentts.cpp direct .NET integration

The temporary native Drogon TTS gateway has been removed from the local runtime.
`Wds.SixStars.Launcher` now supervises only the three inference processes:

- `llama-server.exe` on 8080
- `whisper-server.exe` on 8000
- qwentts.cpp `tts-server.exe` on 8101

The existing .NET speech pipeline remains the protocol/orchestration layer.
`QwenCppHttpStreamingVoiceGenerator` replaces `WebSocketTtsVoiceGenerator` for
the local path. It health-checks qwentts.cpp, registers Star clone WAVs through
`POST /v1/audio/voices`, then sends the existing intentional `TextParts` to
`POST /v1/audio/speech` and raises each returned WAV through the unchanged
`IStreamingVoiceGenerator` events.

The voice-registration cache fingerprints the clone WAV and automatically
re-registers/retries once if qwentts.cpp reports an unknown voice after a native
server restart.

`config/runtime-manifest.json`, the installer, runtime-pack layout, launcher
environment, UI defaults, and current runtime documentation no longer require a
gateway executable or port 8100.

# Push-To-Talk Loop + Parallel Streaming TTS Update

## Summary

This update makes the desktop harness easier to use as a live voice companion test rig while keeping the logic reusable from Unity.

## Defaults

- `WebSocketTTS.ChunkGenerationMode` now defaults to `parallel`.
- `WebSocketTTS.MaxParallelChunks` defaults to `0`, meaning all chunks are generated immediately and returned in order.
- `VoiceOutput.AudioBackend` now defaults to `directsound`.
- `Companion.DefaultStarId` now defaults to `Elusiaa` and drives the selected Star at startup.

## Push-to-talk / voice loop

The UI now has a push-to-talk recording button:

- **Start Recording (Win+Ctrl+E)** starts raw microphone capture for the selected Star.
- **Done / Submit Recording (Win+Ctrl+E)** stops recording, sends the buffered audio to Whisper, sends the transcript to the selected Star, speaks the reply, then automatically starts listening again.
- **Cancel** while recording discards the buffered audio.
- **Stop** during processing cancels Whisper/LLM/TTS/playback and exits the voice loop.
- **Win+Ctrl+C** cancels/stops all runtime work.

Manual recording disables wake-word gating for that push-to-talk session, so whatever you record becomes the conversation turn for the selected Star.

## Runtime cancellation API

Added `SixStarsRuntimeOperationController` in the API project.

Hosts can call:

```csharp
CancelAllRuntimeOperations("reason")
```

This cancels the active runtime token and invokes registered cancel actions such as:

- microphone/input cancellation
- LLM request cancellation through propagated tokens
- WebSocket TTS generation cancellation
- queued playback cancellation

This is intended for important game events such as pause, scene transition, combat interruption, or changing active companion.

## Streaming audio queue extension point

`StreamingStarVoiceResponsePipeline` now buffers WebSocket chunks by chunk index and consumes them strictly in order. It exposes:

```csharp
ChunkReadyForConsumer
AutoPlayChunks
PausePlayback()
ResumePlayback()
CancelCurrentSpeech()
```

Desktop mode keeps `AutoPlayChunks=true`, so ordered chunks are played and removed from the queue by default.

Unity can set `AutoPlayChunks=false`, subscribe to `ChunkReadyForConsumer`, and enqueue the WAV bytes into a Unity audio system while still using the same ordered WebSocket/TTS pipeline.

`PausePlayback()` pauses before the next chunk is played. It does not pause a WAV chunk in the middle; it is designed for safe game pause boundaries between queued audio chunks.

## Selected Star memory reset

The UI now has **Clear Selected Star History**, which resets local memory/relationship state for the Star currently selected in the Star dropdown. The default selected Star is configurable:

```json
"Companion": {
  "DefaultStarId": "Elusiaa"
}
```

Override it with:

```bat
set SIX_STARS_DEFAULT_STAR_ID=Kaelia
```

## Python server chunk mode

The WebSocket TTS client now sends these values in the request payload:

```json
"chunkGenerationMode": "parallel",
"maxParallelChunks": 0
```

The Python server honors per-request values when present, then falls back to environment variables:

```bat
set QWEN_TTS_CHUNK_GENERATION_MODE=parallel
set QWEN_TTS_PARALLEL_CHUNKS=true
set QWEN_TTS_MAX_PARALLEL_CHUNKS=0
```

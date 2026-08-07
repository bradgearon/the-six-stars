# TTS audio copy-to-CPU owned buffer fix

## Why
Generated Qwen/FasterQwen audio may be returned as CUDA-backed tensors or framework-owned arrays. The desktop app, phone audio socket, saved Android voice file, and any other listening client should not share those buffers. They should only receive normal WAV bytes copied from a stable CPU array.

## Changed
- `scripts/websocket_tts_server.py`
  - `waveform_to_float32_numpy` now immediately materializes torch tensors as contiguous `float32` CPU data with `copy=True`, then copies the resulting numpy array again so the WAV encoder owns independent memory.
  - CUDA tensors are synchronized before the CPU copy.
  - Meta tensors now produce a clear model-loading error instead of pretending audio exists.
- `scripts/qwen2_audio_tts_server.py`
  - Legacy conversion path now uses the same CPU materialization/copy pattern.
- `StreamingStarVoiceResponsePipeline`
  - Incoming WebSocket audio chunk bytes are copied into an owned buffer before queuing.
  - Ordered consumer/playback results receive another owned byte array copy.
- `MobileBridgeAudioSocketHost`
  - Phone fan-out copies each WAV byte array before checksumming and framing it for Android clients.
- `MainViewModel`
  - Story prefetch chunk handoff copies WebSocket audio bytes before storing them in playback results.

## Result
The model still greedily uses CUDA for inference. Once audio is generated, it is cheaply copied to CPU/managed memory immediately, encoded to WAV bytes, and then distributed to desktop/Android listeners safely.

# CUDA-only TTS restore and no-CPU fallback update

This update reverts the earlier Qwen TTS meta-tensor/serialized-generation experiment and restores the working parallel chunk behavior used by the main desktop voice path.

## What changed

- Restored `scripts/websocket_tts_server.py` to the previous parallel Qwen streaming behavior.
- Removed the serialized model generation lock retry path and the meta-tensor safe fallback logic.
- Restored Qwen chunk defaults:
  - `QWEN_TTS_CHUNK_GENERATION_MODE=parallel`
  - `QWEN_TTS_PARALLEL_CHUNKS=true`
  - `QWEN_TTS_MAX_PARALLEL_CHUNKS=0`
- Enforced CUDA for TTS. If PyTorch cannot see CUDA, the Qwen TTS server exits with a clear hosted-service message instead of silently using CPU.
- Enforced CUDA for source faster-whisper ASR.
- Enforced CUDA for the optional source local LLM server.
- Updated `config/runtime-manifest.json` with a `gpuPolicy` section:
  - `requireCuda=true`
  - `allowCpuFallback=false`
- Updated launcher runtime selection so LLM, ASR, and TTS select only NVIDIA/CUDA packs by default.
- Updated Android default bridge URL to `http://192.168.1.217:8066`.

## Why

The Android phone audio socket was already proven to receive and play WAV bytes. The failure was happening inside Python Qwen TTS generation, not in Android playback. The voice path is now back to the simpler intended model:

Desktop or Android request -> Qwen TTS WebSocket service -> WAV chunks -> subscribed client playback.

The Python service may still move final generated tensors to CPU briefly when converting them into WAV bytes. That is not CPU inference; it is normal byte serialization for audio output.

## Configuration

Use `config/runtime-manifest.json` for setup changes. Environment variables are only launch-time plumbing between the .NET launcher and child service processes.

To allow CPU fallback later, edit:

```json
"gpuPolicy": {
  "requireCuda": false,
  "allowCpuFallback": true
}
```

For this build, CPU fallback is intentionally off.

# TTS Fast Qwen3 CUDA Graph Parity Update

## Goal
Keep the AI-provided `spokenText` array as the source of truth for TTS chunking, keep DirectSound as the default desktop playback backend, and make the WebSocket Qwen3-TTS server match Voice Clone Studio's fast 0.6B CUDA graph path.

## What changed

- Restored sentence/breath chunk behavior:
  - `StreamingStarVoiceResponsePipeline` now sends `response.SpokenParts` as `TextParts` again.
  - Local story autoplay/prefetch requests also send `response.SpokenParts` as `TextParts`.
  - The WebSocket client no longer sends `maxWordsPerChunk` / `maxSentencesPerChunk` when `TextParts` are present.
- Kept `Qwen/Qwen3-TTS-12Hz-0.6B-Base` as the default model.
- Restored `directsound` as the default `VoiceOutput.AudioBackend`.
- Updated `websocket_tts_server.py` to use the same acceleration path as Voice Clone Studio when available:
  - imports `FasterQwen3TTS` from `faster_qwen3_tts`
  - loads with `FasterQwen3TTS.from_pretrained(..., device="cuda", dtype=torch.bfloat16, attn_implementation="eager")`
  - lets the faster backend warm up and capture predictor/talker CUDA graphs internally
  - uses direct `ref_audio` / `ref_text` voice clone calls for the faster backend
  - falls back to normal `qwen_tts.Qwen3TTSModel` only if `faster_qwen3_tts` is unavailable
- Added `QWEN_TTS_USE_FASTER_QWEN3=true` defaults in launchers.
- Added startup auto-install attempt for `faster-qwen3-tts` when the venv marker already exists.
- Added `faster-qwen3-tts` to Python requirements.
- Rewrote `scripts/test_elusiaa_tts.py` to use the WebSocket server instead of the old HTTP `/v1/audio/speech` endpoint.

## How to verify

Start the backend and make one TTS request. In `logs/services/websocket_qwen_tts.log`, the fast path should show lines similar to:

```text
Loading Qwen3-TTS with FasterQwen3TTS CUDA graph backend (Voice Clone Studio parity).
[OK] Model loaded with CUDA graphs acceleration
Warming up predictor (3 runs)...
CUDA graph captured!
Warming up talker graph (3 runs)...
Talker CUDA graph captured!
Using FasterQwen3TTS direct ref_audio voice clone path
```

The WebSocket status message now also reports:

```json
{
  "cudaGraphsRequested": true,
  "cudaGraphsActive": true,
  "fasterBackend": true,
  "torchCudaAvailable": true
}
```

If `fasterBackend` is false, the package is missing or CUDA is unavailable, and generation will be slower.

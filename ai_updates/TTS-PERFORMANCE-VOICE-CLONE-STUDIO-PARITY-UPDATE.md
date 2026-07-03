# TTS Performance / Voice Clone Studio Parity Update

This update aligns Six Stars speech generation more closely with the fast Voice Clone Studio path the user tested.

## Main changes

- Default Qwen TTS model changed from `Qwen/Qwen3-TTS-12Hz-1.7B-Base` to `Qwen/Qwen3-TTS-12Hz-0.6B-Base`.
- WebSocket TTS default chunk size increased from 30 words / 2 sentences to 90 words / 4 sentences.
- Normal foreground speech now sends the joined spoken reply as one TTS request instead of forcing one TTS generation per `spokenText` sentence part.
- Cached story section playback also sends each local story section as one TTS request instead of one request per internal sentence split.
- Default desktop audio backend changed from `directsound` to `wasapi` to avoid the repeated DirectSound failure/fallback overhead seen in logs.
- Backend launcher now sets:
  - `QWEN_TTS_MODEL_PATH=Qwen/Qwen3-TTS-12Hz-0.6B-Base`
  - `QWEN_TTS_CHUNK_GENERATION_MODE=first_then_prefetch`
  - `QWEN_TTS_PARALLEL_CHUNKS=false`
  - `QWEN_TTS_MAX_PARALLEL_CHUNKS=1`
  - `QWEN_TTS_USE_CUDA_GRAPHS=true`
  - `QWEN_TTS_CUDA_GRAPH_WARMUP_RUNS=3`
- The WebSocket TTS server now tries to pass CUDA graph flags to compatible `qwen_tts` builds and calls known CUDA graph helper methods if they exist.

## Why

Voice Clone Studio generated the same kind of Elusiaa line quickly because it used the smaller Qwen3 0.6B model with CUDA graphs and generated the whole phrase in one request. The app was commonly using the 1.7B model and could split small replies into multiple TTS generations, which adds model overhead repeatedly.

## Notes

The CUDA graph hooks are compatibility-safe. If the installed `qwen_tts` package does not expose CUDA graph helpers, the server logs that and continues normally.

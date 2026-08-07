# TTS startup preload and first phone response recovery

## Problem

On a fresh launch, Android could receive the Star's text response while voice never arrived or appeared to hang. The Qwen WebSocket service lazy-loaded the model only after that first response requested TTS. If the WebSocket connection or model generation failed before a completion message, the ordered streaming consumer could also wait forever for chunks that would never arrive.

## Changes

- Qwen3-TTS now begins loading the generation model and CUDA/FasterQwen warmup immediately after the WebSocket listener starts.
- `QWEN_TTS_PRELOAD_MODEL=true` is supplied by both the .NET launcher and Python services launcher by default. Set it to `false` only to restore lazy loading.
- The source `start.cmd` path also sets the preload flag explicitly before launching services.
- Both the .NET runtime supervisor and the Python services launcher start TTS before LLM and ASR, giving Qwen the entire LLM/Whisper startup window to load and warm before the UI opens.
- A request arriving during preload shares the same model-load lock instead of starting a second load.
- TTS connection status now reports whether the model is loaded/loading and any preload error.
- The WebSocket TTS client retries startup connections using its configured retry settings.
- Streaming TTS generation errors now mark generation complete, drain any already-produced chunks, then return an explicit error instead of leaving an empty playback loop alive indefinitely.
- Any early connection/send failure cancels and joins the playback consumer before the request unwinds.
- Android now receives an explicit voice-socket error when a background TTS request fails before producing audio, instead of only showing text while the desktop logs the exception.

## Expected behavior

The TTS Python process should begin using CPU/GPU during application startup rather than waiting for the first Star response. By the time the first Android LLM response is ready, Qwen should normally already be warm. If TTS is unavailable or fails to load, the phone/desktop request now ends with a visible TTS error instead of appearing permanently stuck after text delivery.

# Parallel WebSocket TTS Chunk Generation Update

Updated `scripts/websocket_tts_server.py` so WebSocket TTS starts generation for all text chunks immediately and still sends chunks back to the WPF app in playback order.

## What changed

- Text is still split into ordered chunks.
- The server now creates generation tasks for all chunks at the start of the request.
- If chunk 2 or chunk 3 finishes before chunk 1, the server keeps it buffered.
- The server sends audio to the app only when the next chunk in order is ready.
- The WPF playback loop can keep using ordered chunk playback and does not need to understand out-of-order generation.
- Voice clone prompt caching is protected with a lock so parallel chunk requests do not build the same Star clone prompt more than once.

## Defaults

Parallel chunk generation is enabled by default:

```bat
set QWEN_TTS_PARALLEL_CHUNKS=true
```

By default, every chunk starts immediately:

```bat
set QWEN_TTS_MAX_PARALLEL_CHUNKS=0
```

`0` means all chunks. If GPU memory gets tight, cap it:

```bat
set QWEN_TTS_MAX_PARALLEL_CHUNKS=2
```

To return to the old sequential behavior:

```bat
set QWEN_TTS_PARALLEL_CHUNKS=false
```

## Expected log shape

```text
Parallel chunk generation enabled: starting 3 chunk tasks, max_parallel=3. Chunks will still be sent to the client in order.
Generating chunk 1/3...
Generating chunk 2/3...
Generating chunk 3/3...
Generated chunk 2/3...
Generated chunk 1/3...
Sent chunk 1/3 in order...
Sent chunk 2/3 in order...
Generated chunk 3/3...
Sent chunk 3/3 in order...
Parallel generation complete...
```

## Notes

This improves latency when a response has multiple chunks because later chunks can be prepared while the first chunk is still being generated or played. If the local Qwen model or GPU build is unstable with fully parallel generation, set `QWEN_TTS_MAX_PARALLEL_CHUNKS=2` or `QWEN_TTS_PARALLEL_CHUNKS=false`.

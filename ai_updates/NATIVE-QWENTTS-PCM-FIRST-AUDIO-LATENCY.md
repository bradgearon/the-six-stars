# Native qwentts.cpp PCM first-audio latency update

## What changed

- The local qwentts.cpp adapter now requests `response_format=pcm` by default.
- qwentts.cpp streams raw 24 kHz mono PCM while it is generating. The .NET adapter groups the stream into a 480 ms first window and 960 ms later windows, wraps each window in a normal PCM16 WAV, and feeds the existing desktop/Android playback pipeline immediately.
- Existing consumers therefore remain WAV-compatible, but first playback no longer waits for the entire first sentence to synthesize.
- The Android player no longer adds its 70 ms final hardware-drain guard between windows of an active desktop stream.
- The mobile live-voice pipeline now validates the final number of generated audio windows rather than assuming one WAV equals one LLM sentence.

## Timing diagnostics

The qwentts.cpp HTTP adapter writes diagnostics for PCM POST begin, response headers, first PCM bytes, first playable PCM/WAV window, and stream completion.

The Android bridge also records `mobile.first-audio-ready` and a visible `First Qwen audio ready` journal entry. It reports milliseconds from the first punctuation-complete sentence being queued to Qwen and whether Gemma had already reached EOS. This directly verifies whether playable speech overlaps foreground LLM generation on a real run.

## Configuration

Defaults:

```json
"ResponseFormat": "pcm",
"PcmFirstChunkMilliseconds": 480,
"PcmChunkMilliseconds": 960
```

Environment overrides:

- `SIX_STARS_QWENTTS_RESPONSE_FORMAT=pcm|wav`
- `SIX_STARS_QWENTTS_PCM_FIRST_CHUNK_MS=480`
- `SIX_STARS_QWENTTS_PCM_CHUNK_MS=960`

`wav` remains as a compatibility fallback.

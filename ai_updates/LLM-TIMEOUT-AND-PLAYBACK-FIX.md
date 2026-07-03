# LLM Timeout and Playback Fix

This update fixes two issues seen in the desktop harness:

1. Local LLM requests were falling back to `MockStarAgent` too quickly when a local model needed extra time to finish returning/serializing structured JSON.
2. Streaming TTS could save a correct WAV file but not audibly play it because room/mic noise could cancel the shared speech token before playback, or because the old playback endpoint only used `WaveOutEvent`.

## What changed

### Longer local LLM wait

`LLM` config now supports:

```json
"LLM": {
  "TimeoutSeconds": 600,
  "MaxTokens": 8192,
  "Temperature": 0.65
}
```

Environment overrides:

```bat
set SIX_STARS_LLM_TIMEOUT_SECONDS=600
set SIX_STARS_LLM_MAX_TOKENS=8192
set SIX_STARS_LLM_TEMPERATURE=0.65
```

The WPF status now says that it is waiting for the real LLM and shows the timeout. This is expected for local models that keep logging token/speed status after the visible generation bar reaches 100%.

### Safer interruption behavior

The app no longer cancels Qwen/LLM generation just because the microphone hears room noise while the Star is preparing a reply.

Default interruption behavior is now:

- Do not cancel generation from mic noise.
- Allow interruption during actual playback only.
- Require a longer, clearer user speech segment before interrupting.

### More reliable Windows audio playback

`NaudioWavePlaybackEndpoint` now uses the Windows default WASAPI multimedia render endpoint first, then falls back to `WaveOutEvent`.

The generated WAV still saves first, then the same bytes are played.

## Good quick test

1. Start `websocket_tts_server.py`.
2. Start the WPF app.
3. Type a normal message to Elusiaa.
4. Wait for the LLM to finish. Do not restart when the model hits 100%; local servers can still be serializing the JSON.
5. Verify the status says `REAL LLM Response` and then `Playing chunk...`.

If the WAV file saves and the app still cannot be heard, check the Windows default output device and volume mixer for `six-stars`.

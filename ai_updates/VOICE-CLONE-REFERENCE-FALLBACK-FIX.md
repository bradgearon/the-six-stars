# Voice Clone Reference Fallback Fix

## Problem

Occasionally Qwen TTS failed with:

```text
VoiceClone requires a reference audio file. Provide voiceCloneAudio/voice_clone_audio, or put the Star clone file in src/Wds.SixStars.Api/Content/VoiceClones.
```

The failure could happen when an LLM-provided voice directive invented a clone file such as `elusiaa-dev.mp3`, `elusiaa-dev_stable`, or another non-existent reference name. When that happened, the Python TTS server tried the bad request value and did not fall back to the trusted Star profile value like `elusiaa_clone.wav`.

## Fixes

- The foreground speech pass remains spoken-text only.
- The API now treats clone audio and clone reference text as trusted Star profile configuration, not model output.
- The full/background JSON schema no longer exposes `voiceCloneAudioFile`, `voiceCloneRefText`, or `xVectorOnly` for the LLM to invent.
- The WebSocket TTS server now resolves clone audio in this order:
  1. requested clone path, if it exists
  2. selected Star profile clone file
  3. inferred common names like `elusiaa_clone.wav`
  4. multiple likely clone folders, including `src/Wds.SixStars.Api/Content/VoiceClones`, `Content/VoiceClones`, and `VoiceClones`
- If a requested clone file is missing but the Star profile clone exists, the server logs a warning and continues instead of failing speech.

## Expected Elusiaa setup

Place the clone WAV at one of these paths:

```text
src/Wds.SixStars.Api/Content/VoiceClones/elusiaa_clone.wav
Content/VoiceClones/elusiaa_clone.wav
VoiceClones/elusiaa_clone.wav
```

The Star profile already points to:

```json
"voiceCloneAudioFile": "elusiaa_clone.wav"
```

# Voice Clone Request-Only Python Server Update

The WebSocket Qwen TTS server no longer performs any Star JSON or voice-clone file lookup.

## What changed

- `scripts/websocket_tts_server.py` no longer reads Star profile JSON files for voice settings.
- The server no longer has clone-audio search directories.
- VoiceClone generation now requires the WebSocket request to include `voiceCloneAudio` as a full absolute path.
- The WPF host/MainViewModel remains responsible for resolving the selected Star's clone file before constructing the TTS request.
- Python only validates that the supplied path is absolute and exists.
- Python builds voice instructions only from fields in the WebSocket request, such as:
  - `voiceDesignPrompt`
  - `deliveryInstructions`
  - `primaryEmotion`
  - `pace`
  - `volume`
  - `pitchDescription`
  - `prosody`
  - optional `personalityShort`, `voiceArchetype`, `speakingStyle`, `voiceStyleRules`, and `voiceGenerationRules` if a host sends them later.

## Why

This keeps the Python TTS sidecar deterministic and makes it reusable from both the desktop app and Unity. The host chooses the Star, resolves the local file path, and sends the full request. The Python sidecar only generates audio from the request it receives.

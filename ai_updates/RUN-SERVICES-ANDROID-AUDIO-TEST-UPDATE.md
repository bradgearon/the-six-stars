# Run services only + Android phone audio test update

This update separates service startup from desktop UI startup and tightens the Android mobile bridge audio path.

## Scripts

- `run-ai-companion.cmd` now starts only the local AI services and keeps them running in the command window. It passes `-NoUi` to the launcher.
- `run-ai-services.cmd` is an explicit alias for services-only startup.
- `run-ai-companion-with-ui.cmd` preserves the old behavior: start services and launch the WPF UI.
- `launch-ui-only.cmd` launches only the WPF UI and assumes the LLM, Whisper, and TTS services are already running.

Recommended workflow:

1. Run `run-ai-companion.cmd` and leave it open.
2. Run `launch-ui-only.cmd` when you want the desktop app.
3. Use the Android app against the desktop bridge URL, usually `http://<pc-ip>:8066`.

## Android bridge fixes

- Android response text now parses both camelCase and PascalCase JSON fields, so `spokenText` / `SpokenText` and `starOutput` / `StarOutput` both display.
- Android debug output is clipped and only shows basic status instead of full bridge payloads.
- Android now has a `Test Phone Audio` button:
  - first queues a local 24 kHz PCM WAV beep to verify phone `AudioTrack` playback;
  - then calls `/api/mobile/test-audio` so the desktop sends a synthetic 24 kHz PCM WAV through the phone audio socket.
- The desktop HTTP bridge now returns the Star text response immediately and generates/sends Android voice chunks in the background over the phone audio socket. This prevents the Android response panel from waiting on TTS.
- Background emotion/memory analysis is still released after Android voice generation/sending, or after the no-audio path.

## Code cleanup

- Fixed the `StarPromptBuilder` story-history loop variable conflict by using `toldStory` in the memory block.
- Fixed the Android recorded WAV header construction.

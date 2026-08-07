# Android Audio Checksum, Saved Voice, and Debug Update

This update improves the Android phone bridge voice path after the phone test tone succeeded.

## Desktop/WPF

- `MobileBridgeEnabled`, `MobileBridgeStatus`, and `MobileBridgeLastRequest` now have public setters so XAML/binding/tooling no longer treats them as readonly-only properties.
- Added `MobileBridgeVoiceDebugLog` and a WPF "Phone voice stream" debug box under the Android bridge panel.
- Desktop audio socket frames now include SHA-256 checksum metadata in the WAV frame header:
  - `sha256`
  - `sha256Short`
  - `bytes`
  - `durationMs`
  - `sentAtUtc`
- Desktop test audio endpoint now returns the checksum and prints it in the UI.
- Desktop sends cute status frames to Android when voice generation starts, chunks are sent, and generation finishes.

## Android

- Added checksum verification for every incoming WAV frame.
- Added a voice stream journal below the main interface with cute Six Stars-style event messages.
- Added `Play Saved Voice` button.
- Incoming desktop WAV chunks are saved into an internal app file named `last-six-stars-phone-voice.wav`.
- Streaming chunks are also accumulated into a combined WAV file as they arrive, so after the final chunk the saved file should represent the latest full phone voice response.
- Playback logs start/finish, sample rate, bytes, duration, and checksum.
- Test Phone Audio now shows local and desktop checksum details.


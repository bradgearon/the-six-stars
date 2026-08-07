# Android Mobile Bridge Client Update

Added an Android debug client and a desktop LAN bridge for phone-based Six Stars testing.

## Desktop WPF changes

- Added `MobileBridge` settings to `appsettings.json` and `AppConfiguration`.
- Added an **Android Phone Bridge** panel to the main debug UI.
- Added `MobileBridgeServer`, a dependency-free `TcpListener` HTTP server.
- Added endpoints:
  - `GET /health`
  - `GET /api/mobile/stars`
  - `POST /api/mobile/message`
  - `POST /api/mobile/audio`
- Text requests go directly into the same `SixStarsRuntime` path as desktop typed requests.
- Audio requests accept base64 WAV/PCM, transcribe through the configured desktop Whisper endpoint, then run the same Star conversation path.
- Responses update the desktop output/debug panes and optionally play through desktop TTS.

## Android project

- Added `android/six-stars-mobile`.
- Native Java Activity, no AndroidX dependency.
- Defaults to `http://grasshoppah:8066` and polls `/health` every 5 seconds.
- Supports Star/request selection, typed message sends, push-to-talk recording, response display, and debug output.
- Added bootstrap Gradle scripts and Windows build/install scripts.

## Build

- Android Gradle Plugin: `9.2.0`
- Gradle bootstrap distribution: `9.6.1`
- compileSdk/targetSdk: `36`
- minSdk: `26`

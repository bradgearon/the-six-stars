# Android Mobile Bridge Quick Start

This archive now includes a phone test client under:

```text
android/six-stars-mobile
```

Start the desktop Six Stars UI first. The right-side debug panel has **Android Phone Bridge** and hosts TCP port `8066` by default. The Android app defaults to:

```text
http://grasshoppah:8066
```

If your phone cannot resolve `grasshoppah`, enter your PC LAN IP in the app, for example `http://192.168.1.25:8066`. Also allow inbound Windows Firewall traffic for TCP `8066`.

Build the debug APK from Windows:

```bat
build-android-mobile.cmd
```

or:

```bat
cd android\six-stars-mobile
build-android-debug.cmd
```

Install with adb:

```bat
cd android\six-stars-mobile
install-android-debug.cmd
```

More details are in:

```text
docs/Android-Mobile-Bridge.md
```

## Timeout / phone voice playback patch

This build includes the fix for the Visual Studio `client was null` exception in `MobileBridgeServer.HandleClientAsync`. That bug made the Android app time out even though the desktop bridge checkbox was enabled.

The Android checkbox now says **Play Star voice on this phone**. When checked, the phone sends `returnAudio=true` and `speakOnDesktop=false`; the desktop returns generated WAV chunks in the JSON response and the Android app plays them locally.

## Phone voice queue update

The Android app now uses two connections:

- HTTP control/API: `http://grasshoppah:8066`
- Phone voice socket: reported by `/health` as `audioPort`, usually `8067`

The desktop receives Qwen/WebSocket TTS chunks, orders them through the streaming voice pipeline with desktop autoplay disabled, and forwards each WAV chunk to Android. Android plays the queued PCM16 WAV chunks locally using `AudioTrack`. The Android debug window is intentionally compact and no longer stores full bridge request/response JSON.

Open both TCP ports in Windows Firewall when testing from a real phone.

## Services-only workflow

`run-ai-services.cmd` starts only the prebuilt local AI engine EXEs. Leave that window open. `run-ai-companion.cmd` is the full prebuilt-engine app launcher again.

Use `launch-ui-only.cmd` to open the desktop WPF app separately. This lets you restart the UI or Android bridge without killing the local LLM, Whisper, and Qwen TTS services.

Old all-in-one behavior is still available with:

```bat
run-ai-companion-with-ui.cmd
```

## Android audio testing

The Android app has a **Test Phone Audio** button.

It does two tests:

1. Plays a local generated 24 kHz PCM WAV beep on the phone. If this works, Android `AudioTrack` output is good.
2. Calls the desktop bridge endpoint `/api/mobile/test-audio`. The desktop sends a synthetic 24 kHz PCM WAV over the phone audio socket. If this works, the desktop-to-phone audio queue is good.

For Android phone voice you need both firewall rules:

```powershell
New-NetFirewallRule -DisplayName "Six Stars Mobile Bridge HTTP 8066" -Direction Inbound -Action Allow -Protocol TCP -LocalPort 8066 -Profile Any
New-NetFirewallRule -DisplayName "Six Stars Mobile Bridge Audio 8067" -Direction Inbound -Action Allow -Protocol TCP -LocalPort 8067 -Profile Any
```


### Launcher split: prebuilt app vs venv services

- `run-ai-companion.cmd` launches the normal integrated/prebuilt-engine companion app path. It calls `launch.cmd`, which prefers `redist\six-stars-launcher.exe` and falls back to the .NET launcher project for development builds.
- `run-ai-services.cmd` is services-only and keeps the desktop app closed. It uses the prebuilt engine EXEs from `config/runtime-manifest.json` through `launch.cmd --skip-install --no-ui`.
- `launch-ui-only.cmd` opens only the desktop UI after services are already running.
- `run-ai-companion-with-ui.cmd` is the dev/source flow that starts `run-ai-source-services.cmd` in a second window and then opens the UI.


## Launcher modes updated: prebuilt app vs source services

- `run-ai-companion.cmd` is the normal full prebuilt companion launcher. It starts the integrated engine services and opens the desktop app.
- `run-ai-services.cmd` is services-only for the prebuilt engine EXEs. It calls `launch.cmd --skip-install --no-ui` and does not launch the UI.
- `launch-ui-only.cmd` opens the desktop UI after services are already running.
- `run-ai-source-services.cmd` is the Python source/venv debug helper for ASR/TTS. It calls `start.cmd`, which activates `.venv` and then runs `python scripts\start_services.py`.
- Manual Python debug flow is still supported: activate `.venv` yourself and run `python scripts\start_services.py`.

## Android Star selector and dispatcher controls

The Android bridge now includes a Star dropdown plus met/ally/history controls. These do not directly mutate desktop UI objects. The phone sends commands to the API-layer dispatcher through `POST /api/mobile/dispatch`, and the desktop `MainViewModel` listens to the dispatcher to apply the change to the runtime/state store.

The Android app subscribes to queued bridge updates by polling:

```text
GET /api/mobile/events?after=<lastSequence>&max=25
```

This lets the phone UI update when the selected Star changes on either side. `GET /api/mobile/state` returns the current selected Star and Android-session Star state for initial hydration.

## Android bridge default LAN address

The Android client now defaults to:

```text
http://192.168.1.217:8066
```

The phone voice stream still uses the desktop voice queue socket advertised by `/health`. The phone receives ordinary WAV bytes and plays/saves them locally.

## TTS owned audio buffer boundary

The TTS service now treats the generated waveform as a strict ownership boundary: Qwen/FasterQwen may generate on CUDA, but the server immediately materializes the waveform into a fresh CPU `float32` array and encodes WAV bytes from that copy. The desktop streaming queue and Android phone socket also copy WAV bytes before queueing or broadcasting. This keeps each listening client isolated from model/framework-owned buffers while preserving CUDA-only inference.

## Clear input + automatic story seed update

The Android message box now starts blank and clears immediately after a text request is queued. Blank text is allowed only for `Story` requests so the selected Star can invent the story herself. Normal chat requests still require typed text or recorded voice input.

## Star memory controls

The Android app now includes **Clear All Data** in addition to the selected-Star history clear. It dispatches through `/api/mobile/dispatch` using `commandType=ClearAllData`, so the desktop dispatcher resets every local Android-session Star state and publishes queued state events back to the phone.

Old-memory recall is handled on the desktop runtime. Android sends a normal message; if the selected Star needs old quotes, markers, lore, or archived conversation details, the runtime performs the information follow-up pass and Android receives the final answer like any other Star reply.

## Android voice stream ordinal reset note

The Android client now resets its active voice-stream guard when the phone voice socket reconnects. It also accepts an explicit `streamAction=start` status frame as a new stream even if the stream ordinal is lower than a previous session. This prevents valid desktop voice chunks from being dropped as stale after the desktop bridge restarts its stream counter.

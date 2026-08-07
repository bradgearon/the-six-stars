# Android Mobile Bridge Phone Socket Voice Queue Fix

This update changes the Android bridge voice path from "return base64 audio in the HTTP response" to a lightweight persistent phone audio socket.

## Why

The desktop app receives generated Star voice from the Qwen TTS WebSocket as ordered WAV chunks through the same kind of queue used by `StreamingStarVoiceResponsePipeline`. The Android client should not wait for a giant HTTP JSON response with base64 audio, and the desktop should not play Android-requested voice locally.

## New behavior

- HTTP bridge remains on the configured port, usually `8066`.
- Phone voice socket listens on `port + 1`, usually `8067`.
- `/health` now reports `audioPort`, `audioSocketConnected`, and `audioClientCount`.
- Android opens a persistent TCP socket to the reported audio port after `/health` succeeds.
- Android requests set `speakOnDesktop=false` and `returnAudio=true`.
- Desktop generates Star TTS using a separate streaming generator and a `StreamingStarVoiceResponsePipeline` with `AutoPlayChunks=false`.
- Ordered `ChunkReadyForConsumer` WAV bytes are sent to the phone socket.
- Android queues and plays the returned PCM16 WAV chunks with `AudioTrack`.
- The Android debug window now shows compact status only and no longer dumps huge request/response JSON.
- The background emotion/memory update is still released after Android voice chunks are generated/sent, so relationship state still settles in the background.

## Ports

Use this in the Android app:

```text
http://grasshoppah:8066
```

Open Windows Firewall for both ports:

```powershell
New-NetFirewallRule -DisplayName "Six Stars Mobile Bridge HTTP 8066" -Direction Inbound -Action Allow -Protocol TCP -LocalPort 8066 -Profile Any
New-NetFirewallRule -DisplayName "Six Stars Mobile Bridge Audio 8067" -Direction Inbound -Action Allow -Protocol TCP -LocalPort 8067 -Profile Any
```

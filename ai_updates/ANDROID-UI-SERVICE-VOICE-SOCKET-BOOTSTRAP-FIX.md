# Android UI Service Voice Socket Bootstrap Fix

## Symptom

After the Android/Desktop UI-service refactor, text and transcripts continued to arrive in the Android app, but Star voice never played on the phone.

## Cause

The Android control WebSocket and phone-audio transport use separate connections.

Before the refactor, `MainActivity.initializeBridgeWebSocket()` called `testConnection(false)` from the control WebSocket `onConnected` callback. That health request discovered the desktop `audioPort` and opened the separate TCP phone-audio socket.

The refactor moved control WebSocket ownership into `MainViewModel` / `BridgeConnectionService`, but it did not preserve that audio-socket bootstrap side effect. The desktop therefore saw zero Android audio clients and returned text without being able to send WAV chunks.

## Fix

`MainViewModel.handleBridgeConnected()` now:

1. Preserves the event subscription behavior.
2. Requests bridge health after the control WebSocket connects.
3. Reads the advertised `audioPort`.
4. Resolves the desktop host from the configured bridge URL.
5. Connects `PhoneVoicePlaybackService` to the separate TCP audio socket when **Play Star voice on this phone** is enabled.

The playback queue is also started before the control WebSocket connection is initiated.

## Result

The refactored service ownership remains intact while restoring the original transport sequence:

`control WebSocket connected -> health/audio-port discovery -> phone audio TCP connected -> WAV chunks queued -> AudioTrack playback`

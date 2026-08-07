# Android Mobile Bridge Timeout + Phone Voice Playback Fix

This patch fixes the Android bridge timeout that happened even when the desktop bridge appeared to be running.

## Fixed desktop timeout / NullReferenceException

`MobileBridgeServer.AcceptLoopAsync` was passing the accepted `TcpClient` into a lambda and then setting the captured local variable to `null`.
That meant `HandleClientAsync` could receive `null`, throw before writing an HTTP response, and the Android app would sit until its request timed out.

The accept loop now copies the accepted client into a stable local before starting the request task.

## Added Android-side voice playback

The Android app now asks the desktop to return generated Star voice audio instead of playing it on the desktop.

Request payload changes:

- `speakOnDesktop: false`
- `returnAudio: true` when the phone checkbox is enabled

Desktop response additions:

- `voiceOk`
- `voiceError`
- `voiceMessage`
- `voiceMimeType`
- `voiceSource`
- `voiceDurationMs`
- `voiceAudioBase64`
- `voiceAudioChunks[]`

The phone plays returned WAV chunks in order with Android `MediaPlayer`.

## Also improved JSON compatibility

The LAN bridge now serializes response JSON as camelCase so the Android client receives `spokenText`, `starOutput`, `voiceAudioChunks`, etc. The Android client also accepts old PascalCase fields as a fallback.

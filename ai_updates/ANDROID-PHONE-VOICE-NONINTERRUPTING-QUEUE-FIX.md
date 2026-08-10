# Android phone voice non-interrupting queue fix

## Symptom

Android could play only the first sentence or first TTS chunk of a Star response, then report that the desktop stream ended and repeatedly reconnect the phone voice socket. The voice journal showed the audio route alternating between IPv4 and IPv6 every few seconds.

## Root cause

`MainActivity.ensureBridgeWebSocket()` called the force-reconnect method before every bridge request. Prepared-speech reconciliation runs frequently, so ordinary requests repeatedly tore down and rebuilt the control WebSocket. Every control reconnect performed another health handshake and could replace the separate phone-audio socket.

The phone-audio replacement then reset stream state, cleared the playback queue, and stopped the active `AudioTrack`. The socket disconnect callback also announced an idle queue even when already accepted audio was still playing.

## Changes

- Routine bridge requests now call `ensureConnected()` instead of forcing a reconnect.
- The editable fallback URL is force-applied only for an explicit manual bridge test.
- IPv4/IPv6 phone-audio route changes are deferred until the active stream and playback queue are idle.
- A transport reconnect no longer clears already accepted audio or stops the active `AudioTrack`.
- Reconnects preserve the logical stream ID and next expected audio sequence, so a resumed stream does not wait forever for chunks that were already played before the disconnect.
- Accepted WAV chunks remain in FIFO playback order even when a newer stream starts or the network route changes.
- Explicit user/service shutdown still clears playback.
- Socket disconnects only report queue-idle when the playback queue is actually idle.
- The phone voice socket reconnects automatically with bounded backoff while preserving queued speech.
- The desktop gives a disconnected phone up to eight seconds to reconnect, rebinds the active stream when it is the sole phone client, and retries an unsent WAV once.
- Interrupted playback now writes a diagnostic entry with the number of PCM bytes written.

## Expected journal behavior

During a normal response, the journal should no longer show repeated `Connecting to ...:8067` entries every few seconds. A route promotion during speech should instead report `Phone voice route change deferred`, and the route should switch only after the queue finishes.

If a real socket drop occurs, already received chunks continue playing and the journal reports `Phone voice reconnect scheduled`. An unexpected `Phone voice playback interrupted` entry now provides enough detail to identify any remaining explicit clear, shutdown, or `AudioTrack` failure.

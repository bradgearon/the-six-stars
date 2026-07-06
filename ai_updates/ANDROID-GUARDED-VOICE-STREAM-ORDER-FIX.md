# Android Guarded Voice Stream Order Fix

This update makes the Android phone voice path guarded instead of a raw broadcast queue.

## Problem

Android story narration could become unstable because the phone would accept every audio frame that arrived on the socket. If an old story was still generating, closing and reopening the app could reconnect to the desktop and receive the tail of the previous Star's speech. New request audio and old request audio could also remain queued together.

## Fix

- Every Android voice request now creates a fresh voice stream id and monotonically increasing stream ordinal.
- Starting a new Android voice stream cancels the previous phone voice stream token.
- The desktop phone audio socket captures the client list at stream start and only sends that stream's WAV frames to those clients.
- A phone that reconnects after a stream already started no longer receives stale leftover WAV frames from that old stream.
- Audio frame headers now include stream metadata: `streamId`, `streamOrdinal`, `audioSequence`, story part index, story chunk count, and TTS chunk index.
- Android accepts only the current stream and drops stale frames.
- Android clears queued/playing desktop audio when a newer stream starts or an older stream is cancelled.
- Android primes the event subscription on startup so old story/voice bridge events do not replay after closing and reopening the app.

## Result

The story prefetch can still generate ahead, but the phone now plays one guarded stream at a time. Old Kaelia/Elusiaa/etc. speech should not continue after reopening the app, and newer requests replace older queued narration cleanly.

# Android persisted-writing voice stream race fix

A persisted-writing performance could show its text while the phone stayed silent when it replaced an older Android voice stream.

## Root cause

The desktop correctly assigns every phone voice stream a monotonically increasing ordinal. When a new stream replaced an older one, the older desktop task could send its delayed `cancel` control after the new stream had already sent `start`.

The Android receiver compared stream ids for audio chunks, but its control handler set `desktopStreamActive=false` before deciding whether the cancel/end control was stale. That stale state transition could:

- make the phone treat the new writing stream as already finished;
- release the voice-stream wake lock;
- restart the prepared at-rest service while the writing voice was starting;
- allow the background request to compete with or cancel foreground work.

## Changes

- Android now ignores `start`, `cancel`, and `end` controls older than the current stream ordinal.
- A stale control no longer emits an inactive stream callback.
- Accepted audio frames reassert that their current stream is active, recovering safely if a start control was missed.
- The prepared at-rest service cannot resume while a writing command is in flight or during the short text-to-voice startup grace period.
- The normal delayed resume explicitly reopens the prepared-Listen idle window only after the writing voice and playback are idle.

The Android voice journal reports ignored controls as `Stale voice control ignored`, including both the stale and active stream ordinals.

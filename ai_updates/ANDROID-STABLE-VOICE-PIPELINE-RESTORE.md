# Android stable voice pipeline restore

This update removes the Android voice-delivery experiments introduced for persisted-writing playback and restores the shared phone voice path used before those changes.

## Why

The writing-specific fixes modified shared infrastructure used by every Android Star reply:

- `TellStarWriting` forcibly disconnected and reconnected an otherwise idle phone audio socket.
- every normal phone voice response could wait up to six seconds before opening its stream;
- the phone's stream-control state machine was changed globally to reject controls using stream-id and ordinal comparisons;
- writing-specific grace timers and delayed background-service restarts were layered around the same playback queue.

Those changes could delay ordinary replies, leave a reconnect thread or stream state stale, and interrupt a stream after text had already arrived.

## Restored behavior

- Ordinary conversation, at-rest speech, and persisted-writing performances all use the same standard `returnAudio` pipeline.
- A writing performance no longer disconnects or refreshes the shared socket before sending its request.
- Voice stream creation no longer pauses every reply for a six-second client wait.
- Android uses the earlier stream acceptance and control handling that was stable before the writing playback patches.
- The two-stage writing generation, performance-context bound, fallback writing response, and all writing state remain unchanged.

This is intentionally a narrow rollback of shared voice transport changes, not a rollback of the writing feature.

# Android Story Voice Sequence Buffer Fix

This update fixes the Android-only voice/story playback corruption where a story could replay a chunk, cut a word, or keep playing stale narration after the phone lagged or reconnected.

## Root cause

Desktop playback was already protected by the ordered `StreamingStarVoiceResponsePipeline`. Android was receiving the desktop-to-phone WAV frames over its own socket and putting them directly into a FIFO playback queue. That meant phone lag, story prefetch, stream replacement, or a reconnect could cause frames to be accepted or played at the wrong time even though the desktop player sounded correct.

Story requests also created two Android voice phases: the quick story-thinking phrase and the generated full story. When the generated story became ready while the thinking phrase was still speaking, the new stream could clear or stop the old stream mid-word.

## Changes

- Android now buffers desktop WAV frames by `audioSequence` before playback.
- Android only releases frames to `AudioTrack` when the next expected sequence is available.
- Duplicate or already-played `audioSequence` frames are dropped.
- Out-of-order future frames are held instead of played early.
- Saved phone voice audio is now built from the ordered playback stream, not from raw arrival order.
- Duplicate `start` controls for the same stream no longer clear the Android queue.
- Mobile story thinking voice and generated story narration can share the same stream.
- The generated story waits for the thinking phrase voice send task to complete before it starts sending story narration on that same stream.

## Intended flow

```text
Android asks for story
→ API starts one shared Android story voice stream
→ thinking phrase voice sends as sequence 0..N
→ generated markdown story completes
→ story narration reuses the same stream
→ Android buffers by audioSequence
→ playback is ordered and duplicates are dropped
```

This keeps desktop-style prefetch behavior while making the phone side guarded against lag and stale frames.

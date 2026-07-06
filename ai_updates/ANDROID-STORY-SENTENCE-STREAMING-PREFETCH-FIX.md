# Android Story Sentence Streaming + Prefetch Fix

This update makes Android voice playback behave more like the desktop pipeline.

## Problem

Android story narration was waiting for an entire story passage voice batch before sending audio to the phone socket. That meant the phone often did not begin playing until all TTS parts for the current passage had already generated, and the next passage was not requested early enough to hide the generation delay.

The Android AudioTrack player also waited for the full clip duration after writing a WAV to AudioTrack. In stream mode, writing already paces part of playback, so this could add an artificial pause between chunks.

## Fix

- Story narration now streams the first ready TTS sentence/chunk to Android immediately.
- The next story passage TTS request is started as soon as the current Android passage begins queueing audio.
- Already-prefetched passages ask for the following passage before sending their WAV chunks to the phone.
- Android no longer sleeps for the full clip duration after each WAV write. It waits only until AudioTrack's playback head reaches the final frame, then immediately starts the next queued WAV.
- Android bridge event polling is faster so story markdown and voice prefetch events appear with less lag.

## Result

The desired flow is now:

```text
Android receives first ready WAV sentence
→ phone starts playing
→ desktop has already asked Qwen for the next passage
→ next WAVs arrive while current speech is still playing
→ Android queue advances with much smaller gaps
```

This preserves the existing desktop story support while moving the Android/game-client path closer to the same low-gap behavior.

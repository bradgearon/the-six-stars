# Android audio playback sync and sprite stability

## Speech animation timing

The production companion no longer switches to `talking` when the player presses Talk/Listen, when recording starts, or when an LLM response arrives.

The phone voice queue now:

1. Parses the WAV.
2. Creates and initializes `AudioTrack` with the builder API.
3. Writes the first PCM buffer so playback is primed.
4. Calls `AudioTrack.play()`.
5. Waits for `AudioTrack.getPlaybackHeadPosition()` to confirm that samples are actually being consumed.
6. Starts the speech-activity marker array relative to the confirmed playback-head position.

The elapsed sample position is subtracted from every marker before it reaches the UI thread, so the animation remains aligned with sound already emitted. Old voice headers without marker data use the remaining WAV duration, but still begin only after the playback head moves.

## Stable Star rendering

`PixelStarAnimationView` draws every state into the same centered viewport at 70% of the available view box. It scales from the full source canvas rather than independently cropping visible pixels, so equal-sized `idle.gif` and `talking.gif` assets render at exactly the same scale and position.

## Android API modernization

The Android app now uses:

- `ImageDecoder` and animated drawables instead of `android.graphics.Movie` on API 28 and later.
- A first-frame GIF fallback on API 26–27 rather than the deprecated Movie API.
- Activity Result launchers instead of `startActivityForResult`, `onActivityResult`, and direct permission callbacks.
- `AudioRecord.Builder` and `AudioTrack.Builder`.
- `ContextCompat.registerReceiver(..., RECEIVER_NOT_EXPORTED)` for the app-only receiver.

Android lint treats the `Deprecated` check as an error so new deprecated API calls fail verification instead of accumulating silently.

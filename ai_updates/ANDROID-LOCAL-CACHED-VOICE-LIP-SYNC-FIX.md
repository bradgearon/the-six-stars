# Android local cached voice lip-sync fix

## Bug

Android's prepared-thought / prefetched-Listen audio is deliberately stored and replayed locally. Those `PhoneVoiceClip` objects use `fromDesktop=false` even though their cached metadata includes the same `speechActivitySegmentsMs` loudness timeline generated from the WAV on the desktop.

`MainActivity.startStarVoiceAnimationMarkers(...)` incorrectly required `clip.fromDesktop`. As a result, the WAV played normally but `StarAnimationCoordinator.beginVoice(...)` was never called, so the production portrait remained on `idle.gif` for locally saved thoughts.

## Fix

Animation playback no longer depends on transport origin. Every phone voice clip now drives `StarAnimationCoordinator.beginVoice(...)` when it begins playing.

- Cached prepared thoughts use their persisted `speechActivitySegmentsMs` markers, so `talking.gif` follows the speech/loudness time segments and returns to idle during gaps.
- Prefetched Listen audio uses the same marker path.
- A local WAV without markers falls back to `talking` for the duration of the clip instead of visibly remaining idle while speech is audible.
- Desktop streamed speech is unchanged.

The important invariant is now: **if Android is audibly playing Star speech, its animation is driven by that audio regardless of whether the WAV arrived live or came from the local cache.**

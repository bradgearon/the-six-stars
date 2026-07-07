# Android Voice Stream Ordinal Reset Fix

## Problem
Android could show the Star text response while playing no audio if the desktop voice stream counter restarted after a socket/app reconnect.

Example symptom from the phone journal:

- desktop starts `android-voice-1` or `android-voice-2`
- Android still remembers an older active stream such as `activeStream=10`
- every new chunk is dropped as stale:
  `incomingStream=1, activeStream=10`

That means the phone received the audio bytes, but the stale-stream guard rejected them before enqueueing playback.

## Fix
`MainActivity` now resets the phone voice stream guard whenever a fresh audio socket connects.

It also treats an explicit `streamAction=start` frame as authoritative even when the new desktop stream ordinal is lower than the previous app-side ordinal. This allows a restarted desktop process/socket to begin again at stream 1 without the Android client dropping valid speech.

## Files touched

- `android/six-stars-mobile/app/src/main/java/com/wds/sixstarsmobile/MainActivity.java`

## Important behavior

The stale-frame guard still protects against old chunks, but it no longer assumes stream ordinals are globally monotonic across desktop restarts or socket reconnects.


# Android talking GIF playback-start fix

The first audio-sync implementation waited only 750 ms for `AudioTrack.getPlaybackHeadPosition()` to advance before giving up. Some Android audio routes can take longer to warm up, so voice audio could play while the talking GIF never started.

## Changes

- Registers a one-frame `AudioTrack` playback marker and starts speech animation when that marker is reached.
- Runs a concurrent startup watcher while PCM continues streaming instead of blocking further writes during route warmup.
- Uses `AudioTimestamp` when available and falls back to the advancing playback-head position.
- Aligns the speech-activity segment array to the playback position detected at actual output start.
- Keeps each clip's cleanup scoped to that clip so a delayed cleanup cannot force a newer clip back to idle.
- Uses only current, non-deprecated Android audio APIs.

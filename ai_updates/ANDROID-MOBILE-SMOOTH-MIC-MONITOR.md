# Six Stars Mobile: smooth live microphone monitor

The optional **Echo microphone while recording** path now uses one persistent `AudioTrack` for the entire Talk recording session.

## Why the first version could sound chopped

The recorder thread wrote each newly captured PCM block directly to `AudioTrack` with `WRITE_NON_BLOCKING`. When Android's output buffer was temporarily full, a partial/zero write could discard part of that microphone block. Because speech naturally alternates between voiced phrases and short pauses, those dropped blocks could sound like playback was being gated between words.

## Updated playback path

- The microphone capture thread remains authoritative and never blocks on speaker playback.
- Captured monitor PCM is copied into a small bounded jitter queue.
- One dedicated audio-priority monitor thread owns one `MODE_STREAM` `AudioTrack` for the whole recording.
- The monitor thread uses `WRITE_BLOCKING`, so normal PCM blocks are delivered contiguously instead of partially dropped.
- A ~one-block preroll smooths startup without introducing a large audible delay.
- The output buffer is sized for about 80 ms where the device minimum permits it.
- If the output device becomes badly delayed, only the oldest queued monitor audio is discarded to keep monitoring close to live; routine timing jitter is absorbed instead of chopped.
- Both the microphone capture thread and monitor writer run at Android audio thread priority.
- Stopping Talk briefly drains the monitor/playback head so the final syllable is not cut off.
- Microphone capture uses 32 ms read blocks even when the device's internal `AudioRecord` buffer is larger, improving monitor cadence while preserving the larger safe recorder buffer internally.

This change is in `android/six-stars-mobile` only. The desktop Speak-as-Star changes from the previous package remain intact.

# Android speech activity marker animation update

This update adds lightweight per-WAV speech activity markers to the desktop -> Android voice socket path.

## What changed

- Desktop analyzes each generated TTS WAV chunk before sending it to Android.
- The analyzer reads PCM16 WAV audio, builds a 10 ms RMS envelope, detects speech bursts, and emits compact `startMs:durationMs` segments.
- The voice socket audio header now includes:
  - `speechActivityVersion`
  - `speechActivitySegmentsMs`
  - `speechActivitySegmentCount`
  - `speechActivityFrameMs`
  - small debug threshold values
- Android parses `speechActivitySegmentsMs` and schedules sprite animation changes while the WAV is actually playing.
- The Star switches to `talking` during each detected speech segment and returns to `idle` between segments / after playback.
- Existing coarse text-based talking pulses remain as fallback for old desktop builds or missing marker headers.

## Why this is efficient

The analyzer is intentionally a single-pass amplitude-envelope detector over PCM16 samples. It avoids FFT allocations and human-readable data. The socket sends only a tiny compact string such as:

```text
20:180;260:240;580:310
```

That means Android does not need to analyze the audio to animate the Star; it only schedules a few UI toggles tied to playback time.

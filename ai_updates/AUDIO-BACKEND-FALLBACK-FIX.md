# Audio Backend Fallback Fix

## Problem

`DirectSoundOut` can throw this exception on some Windows 11/.NET/driver combinations:

```text
System.ArgumentException: Object contains references. (Parameter 'value')
   at System.Runtime.InteropServices.GCHandle.Alloc(...)
   at NAudio.Wave.DirectSoundOut.InitializeDirectSound()
```

This is not a bad WAV file. It happens before normal playback starts inside the DirectSound output path.

## Fix

- The default audio backend is now `wasapi`.
- `auto` now tries `wasapi`, then `waveout`, then `directsound`.
- If `directsound` is selected and it fails, the app falls back to `wasapi`, then `waveout` instead of crashing.
- Unknown backend values now normalize to `wasapi`.

## Override

```bat
set SIX_STARS_AUDIO_BACKEND=wasapi
```

Available values:

```text
wasapi
waveout
directsound
auto
```

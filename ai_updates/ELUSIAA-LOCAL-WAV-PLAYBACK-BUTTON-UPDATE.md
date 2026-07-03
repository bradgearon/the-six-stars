# Elusiaa Local WAV Playback Button Update

Added a **Play Elusiaa Clone WAV** button to the WPF UI under Quick Actions / Voice Output.

The button resolves and plays:

```text
src\Wds.SixStars.Api\Content\VoiceClones\elusiaa_clone.wav
```

It bypasses the LLM, Qwen, WebSocket TTS, HTTP TTS, and generated WAV saving so local Windows/NAudio playback can be isolated.

The UI project also copies `Content/VoiceClones/*.wav` into the WPF output directory when those files exist locally.

Playback debugging now also supports forcing a NAudio backend:

```cmd
set SIX_STARS_AUDIO_BACKEND=wasapi
set SIX_STARS_AUDIO_BACKEND=directsound
set SIX_STARS_AUDIO_BACKEND=waveout
```

Default remains `auto`, which tries WASAPI, then DirectSound, then WaveOutEvent if initialization fails.

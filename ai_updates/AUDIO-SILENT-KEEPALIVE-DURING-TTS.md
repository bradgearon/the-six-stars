# Audio silent keep-alive during TTS generation and playback

This update keeps the desktop audio output warm while Star voice is being generated and spoken.

## Why

Some digital outputs click, sleep, re-handshake, or toggle off/on when the app repeatedly opens an output device for short TTS chunks. The game will normally have background music, but the WPF/dev harness can be quiet between generated chunks. This keep-alive simulates the game music bus by playing an inaudible silent PCM stream while speech is active.

## Behavior

- Starts a silent audio stream when streaming speech generation begins.
- Keeps the stream open while TTS is generating, while chunks are playing, and while waiting between chunks.
- Stops the stream only after the full speech/story operation completes or is cancelled.
- Also covers story autoplay prefetch mode, where the next story chunk is generated while the current one is playing.
- Normal speech chunks are still played exactly as before.
- The default keep-alive backend follows the selected audio backend and defaults to DirectSound.

## Configuration

Environment variables:

- `SIX_STARS_AUDIO_KEEP_ALIVE=true|false` enables/disables the feature. Default: true.
- `SIX_STARS_AUDIO_KEEPALIVE_BACKEND=directsound|wasapi|waveout|auto` overrides the keep-alive backend only.
- `SIX_STARS_AUDIO_BACKEND=directsound|wasapi|waveout|auto` still controls normal speech playback.

## Implementation

- Added `IAudioOutputKeepAliveEndpoint` as an optional playback endpoint capability.
- `NaudioWavePlaybackEndpoint` implements it with a continuous silent 48kHz stereo PCM wave provider.
- `VoiceWavArchivePlaybackEndpoint` delegates keep-alive to the wrapped endpoint.
- `StreamingStarVoiceResponsePipeline` starts keep-alive before calling TTS and disposes it in `finally`.
- Story autoplay prefetch also starts the same keep-alive lease around the whole local story playback operation.

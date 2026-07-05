# Story Prompt Scope and Audio Keep-Alive Check

- Normal chat foreground requests no longer include `StoriesAlreadyTold`/story history.
- The long-story system rules are now injected only when `RequestType=Story`.
- Story requests still include the story history and story mode requirements so the Star can avoid repeating previous tales.
- Normal chat still includes the most recent few player/Star turns for conversational continuity.
- Emotion vectors remain in the background review pass only.
- The existing silent PCM output keep-alive remains enabled by default. `StreamingStarVoiceResponsePipeline` starts it before streaming TTS generation and disposes it only when generation/playback finishes or is cancelled. Story autoplay prefetch does the same for the full cached story playback loop.
- `SIX_STARS_AUDIO_KEEP_ALIVE=false` disables the feature; default is enabled. `SIX_STARS_AUDIO_KEEPALIVE_BACKEND` / `SIX_STARS_AUDIO_KEEP_ALIVE_BACKEND` can override the keep-alive backend.

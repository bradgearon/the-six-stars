# Native qwentts playback hotfix

The experimental `response_format=pcm` path remains available, but it is no longer the default.

The first implementation wrapped the continuous qwentts PCM stream into 480/960 ms WAV windows so the existing Android WAV transport could consume it. Android then created/stopped an `AudioTrack` for each tiny WAV. That is not a truly continuous audio sink and can create chopping, boundary artifacts, or a restart-like sound.

Default behavior is restored to `response_format=wav` while keeping the important latency change: the first punctuation-complete LLM sentence is still handed to Qwen before Gemma reaches EOS. Therefore speech generation can overlap the remaining LLM generation, but each sentence is delivered as one smooth WAV.

For future true PCM streaming, keep one persistent Android `AudioTrack` open for the logical voice stream and write incoming PCM to that same track until stream end. Do not convert sub-second PCM windows into separate WAV playback sessions.

Override remains available for experiments:

`SIX_STARS_QWENTTS_RESPONSE_FORMAT=pcm`

# Desktop Smart DirectSound PCM Playback

Desktop Star speech now mirrors the Android `SmartPcmPlaybackWriter` strategy instead of using WASAPI or treating qwentts transport windows as independent clips.

- local qwentts.cpp desktop speech uses rolling 24 kHz mono PCM again;
- one `DirectSoundOut` and one `BufferedWaveProvider` live for the complete logical response;
- DirectSound latency is 260 ms to give its worker thread scheduling margin while GPU/LLM/TTS work is active;
- playback starts from a learned adaptive reservoir (~900 ms initially, 500-2200 ms range);
- packet generation speed and arrival jitter adjust that reservoir;
- low-water conditions pause the same DirectSound stream and refill it instead of allowing an underrun/reopen;
- the provider returns silence only as a final safety net so DirectSound cannot tear down on a transient producer gap;
- if that safety net is actually reached, only the first few milliseconds after starvation are faded in to avoid a hard zero-to-waveform click;
- successful fast responses gradually lower the learned startup buffer; refill/starvation events raise it;
- WASAPI is not used by the continuous Star speech path. One-shot `directsound`/`auto` playback also no longer silently falls back to WASAPI.

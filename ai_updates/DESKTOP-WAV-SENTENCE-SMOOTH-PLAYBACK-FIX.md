# Desktop WAV sentence smooth playback fix

The remaining desktop crackle was consistent with qwentts.cpp's experimental raw PCM streaming cadence rather than only with NAudio device reopen gaps. Keeping one `BufferedWaveProvider` fixed the large gaps, but it could not remove a discontinuity already present inside the native PCM stream.

## Desktop path

Desktop Star speech now requests `response_format=wav` from qwentts.cpp for each intentional spoken text part. The LLM still streams completed speech parts to Qwen as soon as they are available, so TTS generation overlaps the remaining LLM response. The difference is that each speech part is allowed to finish inside qwentts.cpp's normal WAV synthesis path instead of exposing native sub-frame PCM boundaries to the Windows output device.

All WAV parts for one reply are decoded and appended to one persistent NAudio streaming session. The output device is not reopened between sentences.

The continuous desktop session also:

- waits for about 420 ms of startup audio before beginning when enough audio is available;
- uses a 100 ms output latency window for stability;
- prefers WASAPI shared mode for continuous speech, including when an older config still says `directsound`;
- applies a tiny 4 ms de-click ramp only to complete WAV speech parts, never to rolling PCM transport packets;
- keeps one output alive through inter-sentence pauses and drains it once at the end.

The default desktop audio backend is now `wasapi`.

## Mobile path

Android/mobile behavior is intentionally unchanged. Calls that service the mobile bridge still use the configured qwentts.cpp response format (`pcm` by default) so the phone can retain its rolling AudioTrack low-latency path.

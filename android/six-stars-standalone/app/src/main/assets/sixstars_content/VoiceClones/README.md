# Voice Clone Audio Files

Place your reference clone audio files here.

Recommended format:
- WAV, mono, 24 kHz (or 16 kHz)
- 6-12 seconds
- Dry recording (no music/reverb)
- One speaker only

Each Star JSON file can point to one filename using:
- `voiceCloneAudioFile`
- `voiceCloneRefText`

Example:
- `voiceCloneAudioFile`: `elusiaa_clone.wav`
- `voiceCloneRefText`: exact transcript of that WAV

## Voice design prompt files

Generated prompt packs:

- `docs/Six-Stars-Voice-Design-Prompts.md` — copy/paste prompts and reference text for the Voice Clone Studio UI.
- `src/Wds.SixStars.Api/Content/VoiceClones/six-stars-voice-design-prompts.json` — machine-readable prompt/config pack.
- `docs/Voice-Clone-Setup-Checklist.md` — where to save generated WAV files and which config fields to verify.

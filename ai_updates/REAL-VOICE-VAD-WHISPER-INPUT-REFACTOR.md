# Real Voice VAD + Whisper Input Refactor

This update changes the desktop voice path from mock/open-wake-word style testing into a real microphone → amplitude VAD → Whisper → Star runtime loop.

## Changed

- Real Whisper is now the default path when a Whisper endpoint is configured.
- The app no longer silently uses `MockWhisperSpeechStreamer` unless `SIX_STARS_USE_MOCK_WHISPER=true` is explicitly set.
- OpenWakeWord/audio hotword gating is removed from the desktop voice flow.
- The microphone listens for amplitude above the RMS threshold, captures until silence, then sends the utterance to Whisper.
- Optional wake behavior is now text-based after transcription: `SpeechRequireWakeWord` means the transcript must include a Star wake/name alias.
- Added config-driven voice input settings:
  - `VoiceInput.AlwaysListeningOnStartup`
  - `VoiceInput.RequireWakeWord`
  - `VoiceInput.MinSoundMs`
  - `VoiceInput.SilenceEndMs`
  - `VoiceInput.PreRollMs`
  - `VoiceInput.MaxSegmentMs`
  - `VoiceInput.RmsSpeechThreshold`
  - `VoiceInput.MuteInputWhileStarSpeaking`
- Added reusable API helper `VoiceInputConfigurationFactory.CreateTranscribeOnActivitySettings(...)` for Unity or other hosts.
- Added `NoAudioHotwordDetector` and `UnconfiguredWhisperSpeechStreamer` to make the intended mode explicit.
- Added live input level telemetry from `WhisperSpeechInputPipeline`.
- Updated the WPF layout with a clearer Voice Input panel showing:
  - live RMS level bar
  - threshold/debug line
  - microphone status
  - voice pipeline status
  - last captured segment details
  - last transcript
- Disabled the old HTTP `/v1/audio/speech` desktop generator. WebSocket TTS is the real voice output path; non-streaming mode falls back to mock output only.
- `scripts/start_services.py` no longer starts OpenWakeWord by default.
- `start.cmd` sets:
  - `SIX_STARS_START_OPENWAKEWORD=false`
  - `SIX_STARS_WHISPER_ENDPOINT=http://localhost:8000/v1/audio/transcriptions`
  - `SIX_STARS_REQUIRE_WAKE_WORD=false`

## Runtime Flow

1. Click **Start Listen** or set `SIX_STARS_ALWAYS_LISTENING=true`.
2. NAudio records 16 kHz mono PCM16.
3. `RmsSpeechActivityDetector` watches the incoming audio level.
4. When RMS stays above threshold for `MinSoundMs`, the app starts a speech segment.
5. When silence lasts `SilenceEndMs`, the segment is wrapped as WAV and sent to Whisper.
6. Whisper transcript is either sent to the selected Star directly or filtered by text wake-name detection if enabled.
7. Star response goes through LLM and WebSocket TTS.

## Notes

The simple RMS VAD is intentionally dependency-free so the same API can be reused by Unity. Unity only needs to convert mic samples to PCM16 and call `WhisperSpeechInputPipeline.PushPcm16Async(...)` or `PushAudioAsync(...)`.

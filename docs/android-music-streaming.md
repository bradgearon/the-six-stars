# Progressive Android music playback

Rebuild and deploy both the desktop host and Android six-stars-mobile app together.
Extract this changed-files archive over the existing solution root. Existing music
instruction documents and appsettings are not included or overwritten.

## Behavior
The desktop host composes notation and obtains MIDI from Resonator, then renders
24 kHz mono PCM16 progressively in 250 ms chunks. Android queues each complete
chunk into one continuous AudioTrack instead of waiting for a full WAV download.
Oscillator phase and note envelopes continue across chunk boundaries. A fixed soft
limiter replaces whole-song peak normalization so rendering can begin immediately.

Startup buffering uses request-to-first-chunk latency, then adapts to measured
arrival rate and jitter. Music starts with sufficient buffered audio, pauses when
its lead becomes too low, and resumes after refilling. Cancellation closes the
stream and stops playback. Music and speech share the existing output lock.
The dialog remains open during playback and can cancel the operation.

The current composition and Resonator API still return a complete notation/MIDI
result. This update streams synthesized audio; it does not begin playback during
LLM composition or parse a partially downloaded MIDI file.

## Service boundaries
- MobileMusicStreamingService: composition, MIDI conversion, progressive response.
- MidiPianoWaveRenderer: MIDI timeline and continuous block synthesis.
- MusicStreamClient: HTTP lifetime and cancellation.
- MusicPcmStreamReader: protocol validation and complete-chunk delivery.
- MusicStreamPlaybackService: audio ownership, adaptive buffering and completion.
- SmartPcmPlaybackWriter: shared AudioTrack implementation with a music profile.

POST /api/mobile/music/stream accepts the existing MusicCompositionRequest JSON.
Response is application/x-resone-pcm, close-delimited HTTP. Body starts with ASCII
RSM1, followed by little-endian int32 sample rate, channels and total sample frames.
Each packet contains int32 sequence, int32 byte length and PCM bytes. Sequences
start at zero. A packet of length zero is the terminal marker; total received
frames must match the header. Unexpected EOF, incomplete frames and sequence
errors fail playback. Errors after response headers close the connection without
a terminal marker. The existing full-WAV endpoint remains available.

## Verification
Desktop WPF project built successfully with Windows targeting. Android changed
components compiled against Android API classes; a full APK was not built here.
.NET checks verify first-chunk delivery before later synthesis, streamed PCM
matching WAV samples, MIDI timing and truncated-stream behavior. Java checks read
the actual host-produced protocol fixture in fragmented reads and verify ordering,
complete-chunk gating, missing terminal detection and cancellation.
Real Android AudioTrack playback, adaptive pause/refill and perceived latency
require testing on a phone; no device speed measurements are claimed.

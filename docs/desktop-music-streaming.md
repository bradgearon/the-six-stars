# Desktop music streaming

Apply this patch over the solution that already has Song Mode and the Android
streaming music update. Rebuild the desktop UI. No new settings or packages are
required. This patch does not change the notation instructions or appsettings.

## Playback
Ask for Music, Render MIDI, and Play MIDI now use DesktopMusicPlaybackService.
The service renders and queues 250 ms PCM blocks through the same
MidiPianoWaveRenderer used by Android. The desktop uses a continuous shared-mode
WASAPI output to the default Windows playback device. Preview timbre is now the
shared piano-style renderer, replacing Windows' default MIDI synthesizer. MIDI
remains saved to preview.mid as before.

Synthesis runs on a worker and starts playback once an adaptive reserve is ready.
The reserve uses request-to-first-chunk time, sustained arrival rate and jitter.
Its music profile begins at 500 ms and adapts within 500–2200 ms. Rendering pauses
when the software queue exceeds 2.5 seconds; at most one additional 250 ms block
is queued. This avoids full-song PCM rendering and buffer overflow on long songs.

A low buffer pauses the existing output and waits for refill. Completing input
releases the tail even when it is shorter than the usual startup/refill target.
Stop / Cancel, Clear Song Work, closing the window, and replacement playback cancel
the active session. Replay has a fresh buffer; previous PCM cannot leak into it.

Composition and the Resonator API still produce a complete MIDI file before PCM
playback can begin. The new streaming begins at synthesis, not during composition.

## Reuse and ownership
- MidiPianoWaveRenderer: shared by desktop, streamed mobile and full-WAV mobile.
- DesktopMusicPlaybackService: output lifetime, cancellation, serialization and
  bounded synthesis lookahead, independent of the window.
- SmartDirectSoundPcmPlaybackSession: existing speech buffering engine, also
  supports WASAPI. New raw-PCM input avoids per-chunk WAV encoding/decoding. Music
  gets a separate adaptive profile and waits for refill even at zero buffer.
  Existing speech callers retain their default profile and idle-start behavior.
- MusicWindow: request orchestration and status only; awaits playback completion
  instead of polling the old MIDI player. Generation guards suppress stale status
  updates after cancellation or clearing.

## Validation
The desktop WPF project compiled with Windows targeting. Checks run the production
music service and adaptive buffer with an injected simulated IWavePlayer consuming
real PCM bytes. They cover progressive startup, delayed producer pause/refill,
sample ordering/continuity, short-input drain, bounded rendering, cancellation,
output failure propagation, and replacement playback.

On Windows run:

    dotnet run --project tests/Wds.SixStars.DesktopMusic.Checks

These checks do not open an audio device. In this Linux environment the generated
check-only runtimeconfig's WindowsDesktop framework entry was removed to execute
the managed fake-device checks; production build settings were not changed.
Native WASAPI playback and perceived latency still need a Windows device test.

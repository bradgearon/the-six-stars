# Android music requests

Install this patch into the solution root. Rebuild the desktop app and android/six-stars-mobile. This updates the Android app connected to the desktop bridge, not six-stars-standalone. Start Resonator with start-resonator.cmd and enable the desktop mobile bridge as usual. Existing ResonatorMidi settings and local LLM/Whisper settings are used.

The music icon beside Text opens Ask for Music. Enter a description, duration in bars, tempo in BPM, one meter (4/4, 3/4 or 6/8), and optionally AHD. Submit generates a new song and queues its piano-style audio on the phone. The preview limit is three minutes. Generation errors remain in the dialog for retry; Cancel drops late results.

Speech input uses the same VoiceCaptureService as the main Talk button, including its microphone permission and echoMicWhileRecording setting. Tap again to stop and transcribe with the host's existing Whisper provider. The recognized text fills the description box for review before Submit. Closing the dialog stops its recording and cancels its pending phone request. A disconnected/cancelled phone request can leave bounded host work running until it completes or times out; no late result plays on the phone.

POST /api/mobile/music accepts a MusicCompositionRequest: description, bars, tempo, meter, useAhd (defaults 8, 96, 4/4, true). It runs the shared composer, posts the validated notation to Resonator, and renders the returned MIDI into mono 24kHz PCM16 WAV. The response is audio/wav. The built-in additive piano-style preview needs no soundfont or MIDI device. It interprets notes, velocity, note durations and MIDI tempo; it is not a full General MIDI instrument or VST renderer.

POST /api/mobile/music/transcribe accepts {"audioBase64":"<PCM16 WAV>"} and returns {"transcript":"..."}. This route only transcribes; it does not send a conversation turn to a Star. Recordings are limited to two minutes in the dialog.

The phone plays music through PhoneVoicePlaybackService's existing WAV/AudioTrack path. A shared output lock prevents live PCM speech and WAV music from overlapping. Music clips are not coalesced with speech or used for lip animation. Phone audio Pause/Resume controls apply to queued music too. This is a complete WAV download before playback, rather than live streaming MIDI synthesis.

The shared composer now appends the exact total duration to every request: Bars * numerator * 4 / denominator quarter-note beats. It explains the equivalent quarter-note count while retaining shorter notes, chords, rests and holds. Manually edited instruction documents are not replaced by this patch.

Validation: desktop WPF build; music dialog, capture, network and playback Java compilation against Android APIs; layout XML and adjacent control checks; actual Resonator-generated MIDI to non-silent WAV with tempo, chord, hold, cancellation and invalid-input checks; exact 4/4 and 6/8 duration instruction checks. A packaged APK and physical-phone recording/playback were not tested in this environment.

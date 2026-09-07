# Standalone Android: fast primary review, speech formatting, lip sync, and rough Listen actions

## Why this update exists

The 2026-08-26 device trace proved that the protected primary memory review was **not** waiting for voice playback to finish. For the Kaelia turn in that trace:

- foreground LLM EOS: about 20:43:54.720
- primary review start: about 20:43:54.724
- first WAV playback start: about 20:43:55.401
- final queued WAV playback complete: about 20:44:58.391
- primary review LLM complete: about 20:45:03.754

The review therefore started at Gemma EOS, before the first returned WAV actually began playing, but the hosted JSON request itself remained active for roughly 69 seconds. The scheduling invariant was already correct; the slow path was the hosted JSON-generation request.

## Protected review timing invariant

Normal Chat/voice turns keep this ordering:

1. foreground Gemma streams;
2. the first complete speakable sentence is handed to TTS immediately;
3. Gemma continues to EOS while TTS generation/playback proceeds independently;
4. at EOS, the pending primary review is persisted;
5. the primary review is queued immediately on its protected executor;
6. review LLM output is deterministically committed to Star state;
7. only a *subsequent normal foreground conversation* waits for that commit.

The primary review never waits for the audio playback queue to drain.

## Faster hosted review path

`GoogleAiStudioClient.completeJson` now tries a prompt-enforced JSON request through the same `streamGenerateContent` SSE endpoint used by fast foreground speech, without `responseMimeType=application/json` on the first attempt.

This avoids making the constrained/non-streaming JSON route the default for every protected memory update. The result is parsed as a JSON object locally. If the prompt-only result is malformed, the client falls back to the strict JSON MIME path and finally to the older compatibility request only when necessary.

Tracing now records:

- `llm.stream-json-start`
- `llm.stream-json-first-text` with TTFT
- `llm.stream-json-complete`
- `primary-review.prompt-ready`
- `primary-review.llm-complete`

These events distinguish provider queue/TTFT from actual output generation time without coupling review scheduling to TTS.

## Spoken response formatting

The canonical and standalone `speech_system.sbn` now tell a Star that she is already the speaker and must not put her ordinary dialogue inside literary quotation-mark paragraphs.

Android also applies `SpeechTextFormatter` at the presentation/TTS boundary. It:

- flattens decorative blank-line paragraphs and repeated whitespace;
- removes quote wrappers around complete direct-speech passages;
- preserves `*action beats*`;
- leaves ordinary embedded quoted terms alone.

The normalized final text is what is saved as the Star response and used by the primary review. The final normalized text also replaces the raw streaming fragment in the visible conversation log when EOS arrives.

## TTS generation-ahead queue

TTS synthesis and PCM playback remain separate pipelines. Sentence N+1 begins synthesis immediately after sentence N's WAV has returned and been enqueued; it does not wait for sentence N to finish speaking.

`SentenceChunker` now preserves the low-latency first complete sentence but coalesces later very short sentences where possible. This prevents tiny calls such as `I do play.` and `hungry.` from becoming unnecessary standalone hosted/local TTS requests while retaining immediate first speech.

## WAV speech-activity lip sync

`WavSpeechActivityAnalyzer` is an Android port of the desktop `MobileAudioSpeechActivityMarkers` approach. It performs one inexpensive pass over PCM16 WAV bytes:

- 10 ms RMS loudness windows;
- adaptive high/low thresholds;
- hysteresis around silence;
- small pre/post speech padding;
- short-gap merging and duration limits.

It returns compact start/duration millisecond ranges. `AudioPlaybackService` passes those ranges at actual `AudioTrack` playback start, and `StarAnimationCoordinator` switches the existing `PixelStarAnimationView` between idle/talking states at those boundaries. This works identically for local .NET/Qwen and hosted TTS because it analyzes the WAV returned to Android rather than depending on a provider-specific timing API.

Lip-sync analysis is advisory only: malformed/unsupported WAV data falls back to ordinary whole-clip talking and never blocks playback.

## Per-Star logs

The visible conversation buffer is keyed by Star. Selecting Kaelia shows Kaelia's session conversation; selecting Elusiaa shows Elusiaa's session conversation.

Runtime tracing also keeps the global trace plus automatically routed per-Star files. The Cat console reads only the currently selected Star's trace file.

## Rough Listen buttons

The standalone Listen actions deliberately do not enter the relationship/memory review pipeline:

- **Spark** randomly runs one of the two copied Spark generators: semantic collision or verb/noun displacement. It performs one generation call, speaks all returned writings in order, and does not review/save them.
- **Story / Constellation button** performs one rough foreground story generation for the selected Star, streams/speaks it, and does not run primary review, maintenance, shelf review, or persistence.

These are audition/listening actions, not relationship turns. They must not change Heart, Trust, Romance, connection, memory, daily state, or girlfriend state.

## Compatibility invariant

Nothing in this update changes the canonical deterministic Star-state/import contract. Normal Chat interactions still use the same local state fields and deterministic memory/relationship updater. The special rough Listen actions are intentionally outside that contract because they do not persist state.

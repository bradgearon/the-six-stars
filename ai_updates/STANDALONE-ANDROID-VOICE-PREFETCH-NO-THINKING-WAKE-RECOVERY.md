# Standalone Android: voice prefetch, no-thinking Gemma, and sleep/wake recovery

Date: 2026-08-26

## Purpose

Keep the standalone Android relationship app behavior aligned with the already-working thin-client interaction path:

1. Gemma foreground text streams immediately.
2. The first complete spoken sentence begins TTS synthesis while Gemma is still streaming.
3. TTS generation runs independently from audio playback.
4. As soon as one WAV returns, it is queued for playback and the TTS worker immediately begins the next queued sentence while the current WAV is speaking.
5. The same pipeline is used for local desktop Qwen TTS and hosted Alibaba Qwen TTS.
6. Gemma 4 thinking/reasoning is disabled and must never become part of speech, UI, history, or background-review JSON.
7. After Android sleep/wake, hosted LLM calls wait for Android to restore a validated Internet route and retry one transient DNS/connect failure instead of failing the first request after resume.

## TTS pipeline invariant

Do not put synthesis and playback back onto the same blocking executor. The previous standalone implementation effectively did:

`generate sentence 1 -> play sentence 1 completely -> generate sentence 2 -> play sentence 2`

which guarantees a generation-sized pause between sentences.

The required architecture is now:

`single ordered TTS generation queue -> WAV playback queue/thread`

So while WAV 1 is playing, WAV 2 is already being generated/prefetched. `AudioPlaybackService` intentionally follows the PCM16/AudioTrack queue philosophy from `six-stars-mobile/PhoneVoicePlaybackService` rather than opening a new MediaPlayer/temp file for every sentence.

## Gemma thinking invariant

The standalone app does not use model reasoning. `GoogleAiStudioClient`:

- never sends `thinkingConfig`;
- strips the Gemma 4 `<|think|>` trigger token from every system/user/model prompt before sending;
- ignores provider parts marked `thought=true`;
- defensively strips textual Gemma thought-channel envelopes before live UI/TTS and structured JSON parsing;
- does not add thought content to conversation history.

`ThinkingLevel` is retained only as a backwards-compatible settings field and resolves to `off`; it has no ability to enable thinking in the standalone runtime.

## Sleep/wake network invariant

The standalone app has `ACCESS_NETWORK_STATE` and uses `ConnectivityManager` callbacks, not polling. On resume the Google client's idle connection pool is refreshed. Before a Google request, the client checks for a validated Internet route. If Android is still restoring Wi-Fi/cellular it waits on the default-network callback. A transient `UnknownHostException`, `ConnectException`, `NoRouteToHostException`, or socket failure gets one retry after the route is restored.

This specifically protects the observed wake failure:

`Unable to resolve host generativelanguage.googleapis.com: No address associated with hostname`

The retry is intentionally narrow: one wake/network retry, not a general hidden retry loop.

## State/import boundary

None of these changes alter Star state semantics, deterministic emotion/relationship updates, background-review schema, profile semantics, memory structures, or the standalone-to-game import compatibility contract.

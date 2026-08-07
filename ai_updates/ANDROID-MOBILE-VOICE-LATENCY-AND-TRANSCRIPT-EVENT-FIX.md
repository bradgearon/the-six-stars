# Android Mobile Voice Latency and Transcript Event Fix

## Problem
On Android voice requests, the phone could appear much slower than desktop even though the runtime was still using the fast foreground spoken response and chunked TTS.

The Android app used one `singleThreadExecutor` for everything: the long `/api/mobile/audio` POST, health checks, state refreshes, and `/api/mobile/events` polling. While the voice POST was waiting for upload -> Whisper -> LLM -> HTTP response, the event poller could not run. That meant Android could not show intermediate status such as the transcript being ready.

## Fixes
- Added a separate `requestExecutor` for long text/voice/photo requests.
- Kept a separate HTTP/event executor so bridge events can keep polling while a voice request is in flight.
- Added an in-flight guard for event polling so slow polls do not queue up and block newer polls.
- Desktop now publishes a `MobileTranscriptReady` bridge event immediately after Whisper returns text and before the LLM request begins.
- Android handles `MobileTranscriptReady`, replaces the `[voice message]` placeholder with the actual transcript, and shows `Waiting for her response...` while the LLM is still processing.
- Final voice response handling avoids duplicating the transcript if the early transcript event already inserted it.

## What is still true
- The LLM foreground contract still asks for `spokenText` as an array.
- The desktop voice pipeline still preserves the LLM-provided `SpokenParts` and streams one TTS chunk per part.
- The HTTP response can still show `Voice chunks queued: 0` because voice generation is intentionally happening in the background after the text response is returned. The voice socket journal is the source of truth for chunks sent to the phone.

## Expected behavior
After sending a voice message from Android:
1. The conversation window shows `You: [voice message]` immediately.
2. As soon as desktop Whisper finishes, Android replaces that with the actual transcript.
3. Android shows that it is waiting for the Star response.
4. When the fast foreground LLM reply returns, text appears.
5. Voice chunks stream over the socket and play as they are generated.

# Android First-Sentence Live Voice Restore

## Root cause

The current Android foreground path was no longer actually doing LLM -> TTS overlap. `MobileConversationStreamChunk` still pushed each committed `§` text chunk to the phone immediately, but Build 15N changed `TryStartMobileLiveVoiceAsync` so every speech chunk was only stored in `_deferredTextChunks`. Qwen was not given any text until Gemma reached EOS, and `TransportReady` additionally kept WAV delivery blocked until the final correlated response transport.

That is why the words could appear on Android while the Star stayed silent. The incomplete-looking `§` chunks made the symptom more obvious, but the main latency regression was the explicit post-EOS Qwen gate.

## Fix

- The streaming prompt now defines `§` as a sentence boundary only: it may follow `.`, `?`, `!`, or `…`, never a partial clause.
- `StreamingStarResponseMediator` defensively joins malformed delimiter fragments until terminal sentence punctuation is reached, so TTS receives a natural complete sentence even if the local model still emits `§` too early.
- Android now sends the first complete committed sentence to Qwen immediately while Gemma continues generating.
- Only the first sentence overlaps Gemma. Later speech chunks remain deferred until LLM EOS, preserving most of the Build 15N GPU-throughput protection.
- The existing streamed-text callback runs before first-sentence Qwen enqueue. When the first WAV becomes ready, the desktop sends the Android `start` control and audio immediately. This avoids the older audio-before-text race without waiting for the full final response.
- After Gemma EOS, the remaining speech chunks are batch-enqueued to Qwen and final response transport continues to own cleanup/end-of-stream handling.

## Expected behavior

A healthy normal Chat/Photo/forced-Listen turn should now look roughly like:

1. first complete sentence reaches `§`;
2. Android shows that sentence immediately;
3. first sentence enters Qwen immediately;
4. Android voice `start` + first WAV arrive as soon as synthesis finishes, even if Gemma is still generating;
5. Gemma reaches EOS;
6. remaining sentences are batch-synthesized and play in order;
7. final correlated response arrives and the live voice stream drains/closes normally.

## Validation note

The patch was source-reviewed in this environment. A .NET SDK is not installed in the sandbox, so the C# solution could not be compiled here; run the normal Windows build script / Visual Studio build before packaging the Android deployment.

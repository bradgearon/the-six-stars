# Android Story Adapter + Voice Prefetch Update

## What changed

- Moved cached story-chunk speech traversal into an API-layer adapter: `StarStorySpeechAdapter`.
- Android/game clients now receive queued story events through the mobile bridge dispatcher instead of depending on WPF-only story state.
- Android receives `StoryInitialResponse`, `StoryResponseReady`, `StoryVoicePrefetchStarted`, and `StoryVoiceChunkQueued` events from `/api/mobile/events`.
- A full generated story response includes the complete `storyMarkdown` so Android and future Unity clients can show the whole story while narration is queued.
- Android story narration now starts TTS generation for the next story passage before sending the current passage WAVs to the phone socket. This keeps the next speech warm before current playback starts.
- Deterministic story request promotion now happens when a chat/voice transcript contains a direct story trigger word. Asking Kaelia or another Star for a story no longer depends on the uncertain intent classifier.
- The Android app displays story markdown from both the HTTP response and queued dispatcher events.

## Runtime flow

```text
Android text/voice request
→ runtime detects story trigger or explicit Story request
→ Star immediately speaks a short thinking phrase
→ background story LLM creates full storyMarkdown
→ dispatcher publishes StoryResponseReady with full markdown
→ story adapter chunks markdown locally
→ TTS request for next chunk starts before current chunk is sent to phone playback
→ Android queues and plays WAV frames in order
```

## Notes

The LLM is still responsible for generating the full story markdown once. Chunk-by-chunk speech after that is local adapter work, so desktop, Android, and future game clients share the same continuation behavior without another LLM request for every passage.

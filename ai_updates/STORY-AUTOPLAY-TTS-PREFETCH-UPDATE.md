# Story Autoplay TTS Prefetch Update

This update changes story autoplay so the next cached story chunk begins voice generation while the current chunk is still playing.

## What changed

- Added a dedicated story-autoplay prefetch voice path.
- Story mode no longer waits for one chunk to finish playing before sending TTS for the next chunk.
- The next local chunk is extracted from cached `storyMarkdown`, empty chunks are skipped, and TTS generation is started during current playback.
- No continuation LLM call is sent. The LLM still generates the full story once; local autoplay handles the rest.
- Added debug logs with the prefix `[StoryAutoplayPrefetch]`, including:
  - when empty chunks are skipped,
  - when the next TTS generation request is sent while the current chunk is playing,
  - when prefetched audio is ready.
- Added a separate cancel action for story-prefetch TTS generation.
- Avoided using `SpeechInterruptionManager.StartSpeechGeneration()` for background prefetch generation because that method cancels the active playback token. Playback remains interruptible, while background prefetch is cancelled by runtime/story cancellation.

## Expected behavior

1. User asks a Star for a story.
2. The LLM returns the full chunked `storyMarkdown` once.
3. The app generates and plays the first chunk.
4. While chunk N is playing, the app sends the TTS request for chunk N+1.
5. When chunk N finishes, chunk N+1 should already be ready or mostly ready, reducing dead air.
6. Playback continues until the final non-empty chunk or until the user cancels/interferes.

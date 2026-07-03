# Story Autoplay TTS Prefetch Field Compile Fix

This update fixes compile errors introduced by the story autoplay TTS prefetch pass.

## Fixed

- Added the missing `MainViewModel` fields used by the prefetch implementation:
  - `_playbackEndpoint`
  - `_storyAutoplayPrefetchVoiceGenerator`
  - `_storyAutoplayPrefetchStreamingGenerator`
- Stored the playback endpoint created during startup so the local story autoplay prefetch path can play cached voice batches directly.
- Created a dedicated prefetch TTS generator separate from the normal active voice pipeline.
  - Streaming mode gets its own `IStreamingVoiceGenerator` for background story chunk generation.
  - Non-streaming mode gets its own `IStarVoiceGenerator` for background story chunk generation.
- Kept the normal voice pipeline unchanged for non-story replies.
- Kept the story autoplay prefetch behavior:
  - Generate the current chunk.
  - Start generating the next cached story chunk while the current chunk is playing.
  - Skip empty chunks.
  - Do not send continuation LLM calls.
- Added disposal for the dedicated prefetch generators.
- Tightened nullable assignments in the story voice settings builder.

## Notes

The sandbox used to prepare this package does not have the .NET SDK installed, so `dotnet build` could not be run here. A C# brace/string/comment balance check was run on `MainViewModel.cs` after the patch.

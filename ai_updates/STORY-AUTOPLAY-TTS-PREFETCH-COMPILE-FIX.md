# Story Autoplay TTS Prefetch Compile Fix

Fixes the broken prefetch package where part of `MainViewModel.cs` was accidentally overwritten and several UI command handlers disappeared from the class.

## Fixed

- Restored the missing `MainViewModel` methods:
  - `CancelListeningWithoutSubmittingAsync`
  - `SendAsync`
  - `SendMessageAsync`
  - `TellStoryAsync`
  - `QuickSendAsync`
  - `StartListeningAsync`
  - `StopListeningAsync`
  - `ToggleRecordingAsync`
  - `CancelAllRuntimeOperations`
  - `ClearSelectedStarHistoryAsync`
  - `SimulateSpeechAsync`
  - screen awareness methods
  - manual recording/transcript methods
  - `CreateStoryOptions`
- Kept the story empty-chunk skipping fix.
- Re-applied story TTS prefetch on top of the compile-safe prior version.
- Added a dedicated story prefetch generator so the next story chunk can be generated while the current story chunk is playing.
- Removed the accidental duplicate `SourceResponse = response` initializer from the broken prefetch version.

## Runtime behavior

- First LLM call returns the full story markdown.
- Chunk 0 is generated and played.
- While Chunk N is playing, TTS generation for Chunk N+1 is started.
- Empty chunks are skipped.
- No continuation LLM calls are made during story autoplay.

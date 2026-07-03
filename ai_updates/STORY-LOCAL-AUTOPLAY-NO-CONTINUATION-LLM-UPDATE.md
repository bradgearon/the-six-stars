# Story Local Autoplay / No Continuation LLM Update

## Goal

Story mode should not keep asking the LLM for the next story chunk after the story has already been planned and chunked.

The LLM now does the expensive creative work once:

1. Detect story intent.
2. Generate the complete story in `storyMarkdown`.
3. Speak only `## Chunk 0` in `spokenText`.

After that, the desktop host reads the next cached markdown chunk locally and sends it directly to TTS until the story is finished or cancelled.

## Main behavior change

### Before

After every spoken story section, the UI created another `RequestType.Story` request with:

- `ContinueActiveStory=true`
- `ActiveStoryMarkdown`
- `NextStoryChunkMarkdown`
- a new player text like “continue the active story”

That caused another LLM request for every chunk.

### Now

After the first story response:

- `MainViewModel` keeps `_activeStoryMarkdown`.
- When voice playback completes, `TryContinueActiveStoryAsync(CancellationToken)` extracts `## Chunk N` locally.
- It creates a local `StarInteractionResponse` with `SpokenParts` from that chunk.
- It calls the voice pipeline directly.
- No continuation LLM request is sent.

The debug panel marks these sections as:

```text
LOCAL STORY AUTOPLAY
```

and the response debug note says no LLM request was sent.

## Files changed

- `src/Wds.SixStars.Ui/ViewModels/MainViewModel.cs`
  - Replaced AI continuation calls with cached markdown story autoplay.
  - Added local chunk response creation.
  - Added local story chunk splitting safeguards.
  - Reuses the Star voice directive from the first story response.
  - Interrupt Story cancels cached autoplay.

- `src/Wds.SixStars.Api/Ai/StarPromptBuilder.cs`
  - Updated story prompts so the LLM knows it must generate the full story once.
  - Removed continuation-prompt language from the normal story path.
  - Clarified that the host will auto-play `Chunk 1+` locally.

- `src/Wds.SixStars.Api/Ai/StructuredStarAgent.cs`
  - If a new story contains more chunks after `Chunk 0`, the guard forces `storyIsFinished=false` so the host keeps reading cached chunks.

- `src/Wds.SixStars.Api/Models/StoryOptions.cs`
  - Updated comments to explain that continuation fields are legacy compatibility fields.

- `src/Wds.SixStars.Api/Content/Instructions/main-instructions.json`
  - Updated long-story contract: complete story once, then local cached autoplay.

- `src/Wds.SixStars.Ui/MainWindow.xaml`
  - Updated UI help text to say Interrupt Story stops local cached story autoplay.

## Result

A normal voice or typed message like “Elusiaa, tell me a story” still triggers story mode automatically.

The LLM returns the complete chunked story once, the first chunk is spoken, and the app continues speaking cached chunks until:

- the final chunk is reached,
- the user clicks Interrupt Story,
- the user says a stop/cancel story voice command,
- or a new incoming speech/typed command interrupts the story.

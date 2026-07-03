# Story Chunk Narration Fix

This update tightens long-story mode so the Star actually starts narrating the story instead of saying introductory setup lines.

## Changes

- New story foreground prompts now say the Star is already telling the story, not preparing to tell it.
- `storyMarkdown` is now requested as a chunked narration plan:
  - `## Chunk 0`
  - 3-4 actual story sentences
  - a line containing only `---`
  - `## Chunk 1`
  - 3-4 actual story sentences
  - and so on.
- For a new story, `spokenText` must be the actual prose from `## Chunk 0`.
- For story continuation, the host now sends `NextStoryChunkMarkdown` so the prompt can say exactly what chunk should be narrated next.
- Continuation prompts now explicitly forbid restarting, summarizing, or saying setup phrases like "where we left off" or "let me continue."
- The fast speech parser includes a story guard: if the LLM returns a good `storyMarkdown` plan but the spoken text is intro/mismatched, it replaces the spoken text with the correct markdown chunk so the player hears the actual story.

## New continuation context

`StoryOptions` now includes:

```csharp
public string NextStoryChunkMarkdown { get; set; } = string.Empty;
```

The WPF harness extracts the next `## Chunk N` / `---` section from the active markdown and sends it back with each continuation request.

## Expected story foreground shape

```json
{
  "starId": "Elusiaa",
  "spokenText": [
    "Under the Virethorn roots, a pale little Thornhalo Saint knelt beside a poisoned stream, pretending to pray.",
    "Its halo clicked like a broken music box, and every click made the cave flowers turn their faces away."
  ],
  "storyTitle": "The Thornhalo Saint and the Laughing Harpy Bridge",
  "storyId": "thornhalo-saint-laughing-harpy-bridge",
  "storyMarkdown": "# The Thornhalo Saint and the Laughing Harpy Bridge\n\n## Chunk 0\nUnder the Virethorn roots...\n---\n\n## Chunk 1\n...",
  "storySummary": "A cave-travel story about mercy, mistaken judgment, and a Virethorn creature that learns laughter is not always cruelty.",
  "storyIsFinished": false,
  "storyPartIndex": 0
}
```

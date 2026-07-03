# Story Autoplay Empty Chunk Fix

## Problem
A generated story could contain an empty markdown chunk such as:

```markdown
## Chunk 12
---
```

The local story autoplay reader previously treated the first empty next chunk as the end of the cached story, so narration stopped early even when later chunks existed.

## Fix
- Added local autoplay scanning for the next non-empty `## Chunk N` at or after the current story index.
- Empty chunks are skipped instead of ending the story.
- `StoryIsFinished` for local cached chunks now checks for the next non-empty chunk, not merely the next numeric chunk.
- Story tracking now keeps autoplay enabled when later non-empty chunks remain after blank/delimiter-only chunks.
- Added debug messages when empty chunks are skipped.
- Cleaned trailing inline `---` markers so TTS does not speak dash separators.
- Strengthened story prompts and instruction JSON: every chunk must contain speakable narration; blank chunks are invalid.

## Result
Story mode still uses only the initial LLM story generation. After Chunk 0, the host reads cached storyMarkdown locally and will continue through later chunks even if the LLM accidentally inserted blank chunk headings.

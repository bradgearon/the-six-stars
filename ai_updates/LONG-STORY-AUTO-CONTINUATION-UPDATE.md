# Long Story Auto-Continuation Update

This update adds a long-story mode for the Six Stars desktop harness and shared API.

## What changed

- Foreground story responses can now include a full `storyMarkdown` document on the first story turn.
- The spoken response remains `spokenText: []`, with one or two sentence chunks chosen by the LLM.
- The UI stores the active story markdown and sends it back on continuation requests.
- Story continuation requests ask only for the next few spoken chunks, not a brand-new story.
- The app automatically continues the story after the current speech chunks finish playing.
- The app stops the automatic story loop when:
  - the Star sets `storyIsFinished=true`,
  - the player clicks **Interrupt Story**,
  - the player says/enters a command like “interrupt the story,” “stop story,” or “cancel story,”
  - a new typed or spoken command arrives while a story is active.
- `StarState` now keeps `StoryHistory` entries so each Star can avoid repeating old stories.
- Background memory analysis preserves story title/id/summary and records it in durable state.

## Foreground story schema

New story request:

```json
{
  "starId": "Elusiaa",
  "spokenText": [
    "Oh, little one. Let me tell it softly while you walk.",
    "This story begins under the lower cave lanterns, where the water remembered the stars."
  ],
  "storyTitle": "The Lantern Under the Bone Bridge",
  "storyId": "the-lantern-under-the-bone-bridge",
  "storyMarkdown": "# The Lantern Under the Bone Bridge\n\n...full story...",
  "storySummary": "A gentle cave-travel story about mercy, endurance, and a hidden light.",
  "storyIsFinished": false,
  "storyPartIndex": 0
}
```

Continuation request:

```json
{
  "starId": "Elusiaa",
  "spokenText": [
    "The little lantern did not burn brighter when the cave became afraid.",
    "It burned steadier, and that was the first lesson the stones understood."
  ],
  "storyIsFinished": false,
  "storyPartIndex": 1
}
```

## Environment override

Initial story planning may require more output tokens because the full markdown story is generated up front.

```bat
set SIX_STARS_LLM_STORY_PLAN_MAX_TOKENS=6000
```

The normal foreground chat limit remains:

```bat
set SIX_STARS_LLM_SPEECH_MAX_TOKENS=900
```

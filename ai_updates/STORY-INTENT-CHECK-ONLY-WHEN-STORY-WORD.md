# Story Intent Check Only When Player Says "Story"

## Goal
Avoid sending the extra pre-flight story intent LLM request for normal chat, voice transcripts, design discussion, or any message that does not explicitly contain the standalone word `story`.

## Runtime behavior
- For `RequestType.Chat`, the runtime now checks the raw `PlayerText` first.
- If the text contains the standalone word `story`, it may send the quick story intent classifier request.
- If the text does not contain the standalone word `story`, the story intent classifier is skipped entirely.
- Skipped checks add debug flags:
  - `storyIntentCheckSource=skipped_no_story_word`
  - `storyIntentConfidence=0.00`
  - `storyIntentReason=Skipped story intent LLM check because the player text did not contain the standalone word story.`

## Examples
Classifier is allowed:
- `Elusiaa, tell me a story.`
- `Can you make up a cave story while I work?`
- `Continue the story.`

Classifier is skipped:
- `Elusiaa, stay close while I work.`
- `Tell me a tale.`
- `Keep me company.`
- `What do you think about this conversation system?`

## Files changed
- `src/Wds.SixStars.Api/Engine/SixStarsRuntime.cs`
- `src/Wds.SixStars.Api/Agents/MockStarAgent.cs`

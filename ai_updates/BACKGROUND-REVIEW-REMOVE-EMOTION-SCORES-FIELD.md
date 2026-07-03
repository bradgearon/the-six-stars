# Background Review: Remove emotionScores Field

This update removes `emotionScores` from the long/background review LLM response entirely.

## Why

The background review was still returning a JSON field like:

```json
"emotionScores": { ... }
```

That made the long request larger and duplicated the fast `emotionalEffectVector` experiment.

## New behavior

Fast foreground response still returns:

```text
starId
spokenText[]
emotionalEffectVector
```

Background memory review now returns only durable review fields:

```text
starId
spokenText[]
storyTitle
storyId
storySummary
storyIsFinished
storyPartIndex
detectedCombos
suggestedStateDeltas
conversationSummary
changesSummary
importantPlayerQuotes
memoryNotes
secretsUnlocked
learnedFunctions
relationshipEvents
girlfriendHistoryUpdates
petNamesPlayerUsesForStar
petNamesStarUsesForPlayer
loreUsed
riskFlags
wisdomScore
```

It no longer requests, accepts, or parses `emotionScores` from the background LLM pass.

## Files changed

- `src/Wds.SixStars.Api/Ai/StarResponseJsonSchema.cs`
  - Removed `emotionScores` from full structured and background review schemas.
  - Renamed the compact background schema from `six_stars_background_emotion_memory_review_compact` to `six_stars_background_memory_review_compact`.
- `src/Wds.SixStars.Api/Ai/StarPromptBuilder.cs`
  - Removed instructions asking for impactful emotion scores.
  - Added explicit instructions that background review must not include `emotionScores`.
- `src/Wds.SixStars.Api/Ai/StructuredStarAgent.cs`
  - Updated the background review schema/debug label name.
- `src/Wds.SixStars.Api/Ai/StarStructuredModelResponse.cs`
  - Marked `EmotionScores` as `[JsonIgnore]` so even if a backend ignores schema and emits it, the parser will not use it.
- `src/Wds.SixStars.Api/Content/Instructions/main-instructions.json`
  - Clarified that `emotionalEffectVector` is foreground-only and background review must not include `emotionScores`.

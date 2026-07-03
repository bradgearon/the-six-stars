# Background Review Emotional Vector Cleanup

Removed the compact `emotionalEffectVector` type/field from the old full/long structured response model and schema so the heavy emotional update path stays focused on durable analysis.

## Current split

- Fast foreground response still returns `emotionalEffectVector` for quick testing.
- Background emotion/memory review does **not** request or parse `emotionalEffectVector`.
- Background review still requests compact impactful `emotionScores` with a maximum of 9 emotions.

## Background review fields still requested

The big background review asks for: `starId`, `spokenText`, story metadata, compact `emotionScores`, `detectedCombos`, `suggestedStateDeltas`, summaries, memory arrays, relationship update arrays, lore/risk flags, and `wisdomScore`.

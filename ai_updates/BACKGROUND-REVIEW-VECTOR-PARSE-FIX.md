# Background Review Vector Parse Fix

## Problem
The background review LLM was correctly generating `emotionalEffectVector` and `negativeEmotionVector`, but the runtime treated the response as invalid when `spokenText` was absent.

In the failing log, the background review returned a valid compact review with:

- `emotionalEffectVector`
- `negativeEmotionVector`
- `suggestedStateDeltas`
- `conversationSummary`

But `StructuredStarAgent.Parse(...)` required `spokenText` for every model response. Because background review mode is intentionally not a spoken reply pass, no `spokenText` was returned, so the parser fell back to the salvage response. The salvage response had blank emotion vectors, which made the UI show `Last emotional vectors: none yet.`

## Fix
- Added `requireSpokenText` to `StructuredStarAgent.Parse(...)`.
- Foreground speech/story passes still require spoken text.
- Background emotion/memory review calls now pass `requireSpokenText: false`.
- Background review responses are allowed to parse successfully with no `spokenText`.
- `AnalyzeTurnAsync` continues copying the foreground spoken parts onto the background response after parsing.
- `BuildInteractionResponseFromModel(...)` now copies `EmotionalEffectVector` and `NegativeEmotionVector` immediately so debug notes and UI formatting see them.
- `ApplyCommonJsonAliases(...)` now recognizes snake_case and alternate vector field names.
- The background review prompt/schema were clarified so the model does not need to copy `spokenText` back.

## Expected result
After the background review settles, desktop and mobile should show the actual compact vector values in the Last Emotional Vectors panel/readout.

For example, a compliment like:

> you're so beautiful all the birds outside are singing whenever you are here

may show something like:

`Good: +Affectionate 30 @ 0.10`

Even if the deterministic relationship layer does not award Heart/Trust/Romance yet, the raw vector should still appear.

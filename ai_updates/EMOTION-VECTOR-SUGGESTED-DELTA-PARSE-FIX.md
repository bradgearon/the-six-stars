# Emotion Vector Suggested Delta Parse Fix

## Problem
Desktop/Android UI could show `Last emotional vectors: none yet` even though the background LLM returned a valid `emotionalEffectVector`.

The failing log showed the model returned vectors correctly, but also returned fractional values in `suggestedStateDeltas`, for example:

```json
"suggestedStateDeltas": {
  "Heart": 0.0,
  "Trust": 0.25,
  "Romance": 0.0,
  "BenaveilInfluence": 0.0,
  "EmotionalConnectionPercent": 0.5
}
```

`Heart`, `Trust`, `Romance`, and `BenaveilInfluence` are integer fields in `RelationshipDelta`. `System.Text.Json` rejected fractional numbers for those integer fields, so `StructuredStarAgent.Parse(...)` fell into the salvage path. The salvage path discarded the valid emotion vectors, leaving the deterministic state update with empty vectors and no top emotions.

## Fix
- Added `RelationshipDeltaJsonConverter`.
- It accepts ints, floats, numeric strings, camelCase, PascalCase, and snake_case aliases.
- Fractional Heart/Trust/Romance/Benaveil suggestions are rounded into safe integers instead of crashing the whole parse.
- `EmotionalConnectionPercent` remains a double and keeps fractional values.
- Background parse fallback now preserves `emotionalEffectVector`, `negativeEmotionVector`, and summaries from valid JSON even when another optional field is malformed.
- Background fallback no longer invents a new spoken line when `requireSpokenText=false`; it lets the foreground spoken response remain authoritative.
- Emotion vectors are normalized/padded to the canonical vector lengths before display/scoring.
- The background prompt now explicitly tells the model to use whole integers for Heart/Trust/Romance/BenaveilInfluence and only use `EmotionalConnectionPercent` for fractional movement.

## Expected Result
The desktop Last Emotional Vectors panel should now show the vector/top emotions for turns like:

```text
I want to understand your mercy better, not just receive it
```

instead of falling back to `none yet`.

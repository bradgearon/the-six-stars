# Emotion Vector Array Parse and Display Fix

## Problem
Recent logs showed that the background model was generating valid emotional vectors, but the runtime still displayed `Last emotional vectors: none yet`.

The background JSON included fields such as:

- `emotionalEffectVector`
- `negativeEmotionVector`
- `suggestedStateDeltas`
- `conversationSummary`

However, some local model responses returned `rememberedMarkers: {}` instead of `rememberedMarkers: []`. That malformed list field caused the whole `StarStructuredModelResponse` deserialize to fail. The fallback salvage response then replaced the real background review with a parser-error line and blank vectors.

## Fix
- Added `FlexibleListJsonConverterFactory`.
- Empty objects in list slots such as `rememberedMarkers: {}` are now treated as empty lists instead of parse failures.
- Non-empty objects in list slots are tolerated as single-item lists when possible.
- Scalar strings in string-list slots are tolerated as one-item lists.
- Registered the converter in `StructuredStarAgent` so background memory/vector review, interstar vector review, and related structured responses are more resilient.
- Strengthened the background review prompt so the model is told to emit `[]` for empty list fields and to always include the full emotional vector length.
- Strengthened the emotion vector instruction to use low nonzero confidence for real light reactions instead of leaving the vector completely blank.

## Expected result
The UI should now show observed vector entries such as:

`Good: +Safe 35 @ 0.10 | +Happy 30 @ 0.05 | +Trusting 25 @ 0.05`

These may still be observed-only and not award Heart/Trust/Romance yet. Deterministic relationship points require stronger signals, relevant Star-specific emotion mappings, and/or repeated/combo history.

## Notes
For a Star who is not met or not allied yet, the deterministic layer can still intentionally block Romance/connection movement even when the observed vectors display. That is separate from the UI/vector parse bug.

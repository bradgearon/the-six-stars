# IMPORTANT: SUPERSEDED

This historical update is no longer the active quote policy. As of the Cloudflare/private-analytics/interstar-guard update, important-player-quote significance is selected **rarely by the protected background review LLM** and the deterministic layer only verifies exact current-turn provenance. See `CLOUDFLARE-PROXY-PRIVATE-ANALYTICS-IMPORTANT-QUOTES-INTERSTAR-GUARD.md`. The older behavior is preserved below only as history.

# Important Quote Deterministic High-Emotion Capture

This update removes important-player-quote decision making from the LLM and moves quote capture into the deterministic memory layer.

## Behavior

- `importantPlayerQuotes` remains in the JSON response shape for compatibility, but the schema requires it to be empty.
- Prompt instructions now tell the LLM to always return `importantPlayerQuotes: []`.
- `StructuredStarAgent` ignores any model-provided important quote values, including salvaged JSON responses.
- `StarMemoryApi` no longer stores model-provided important quotes.
- The latest player message is stored as an important quote only when deterministic compact emotion-vector parsing finds a qualifying high-impact emotion.

## Default threshold

A player message qualifies when at least one parsed `TopAffectedEmotion` has:

- `Confidence >= 0.15`
- `Score >= 30`
- `WeightedScore >= 4.5`

The threshold is controlled by `StarMemoryOptions`:

- `AutoImportantQuoteMinEmotionConfidence`
- `AutoImportantQuoteMinEmotionScore`
- `AutoImportantQuoteMinWeightedEmotionScore`
- `AutoImportantQuoteMaxLength`

## Stored record reason

Durable quote records now use reasons like:

`auto-high-emotion-quote:positive:Affectionate:score=55:confidence=0.25:weighted=13.8`

This makes it clear why the quote was captured and keeps ordinary low-impact turns out of the important quote list.

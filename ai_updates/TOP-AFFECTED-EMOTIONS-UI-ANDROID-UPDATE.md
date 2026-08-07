# Top Affected Emotions UI + Android Update

This update exposes the strongest emotion-vector effects from the settled background deterministic state update.

## Added

- `TopAffectedEmotion` API model.
- `StarInteractionResponse.TopAffectedEmotions`.
- `StarState.LastTopAffectedEmotions`.
- `EmotionUpdateHistoryEntry.TopAffectedEmotions`.
- Mobile bridge response/event support for `topAffectedEmotions`.
- Desktop UI binding: `TopAffectedEmotionsText`.
- Android display below the selected Star state line.

## Behavior

After the background emotion/memory review settles, the deterministic layer parses:

- `emotionalEffectVector`
- `negativeEmotionVector`

It sorts all non-zero vector items by weighted score (`confidence * score`) and keeps the top three.

Each item includes:

- emotion id
- valence
- confidence
- score
- weighted score
- whether it was accepted for state update thresholding
- compact summary

The desktop publishes a queued `TopAffectedEmotionsChanged` mobile bridge event so Android can update without waiting for the next message.

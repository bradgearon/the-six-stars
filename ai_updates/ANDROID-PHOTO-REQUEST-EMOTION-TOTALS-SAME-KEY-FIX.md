# Android Photo Request + Emotion Totals + Same-Key Fix

## Why
- Android text/voice requests could fail with `An item with the same key has already been added` when duplicate normalized keys appeared in local information availability or duplicate emotion keys appeared in model output.
- Android needed a camera/photo request path that asks the selected Star to react directly to an attached image.
- Star state needed durable accumulated good/bad emotion totals so the next base request always includes more than the latest one-turn vectors.

## Changes
- Added `RequestType.Photo`.
- Added `StarPhotoAttachment` to `StarInteractionRequest` so Unity/game/mobile hosts can attach photo bytes, mime type, caption, image reference, vision summary, and error.
- Added a `SendPhoto` mobile dispatcher command. Android sends camera photo requests through `/api/mobile/dispatch` instead of treating them as plain text.
- Added Android `Share Photo` camera flow. It sends JPEG bytes as base64, clears the text box, and receives a normal Star response + phone voice stream.
- Added photo-specific prompt guidance: the Star reacts as herself using lore, personality, relationship/emotional state, and the attached image summary.
- Added `PromptOverride` to the vision summarizer so photo attachments are summarized as photos, not desktop screenshots.
- Added `PositiveEmotionTotals` and `NegativeEmotionTotals` to `StarState` with durable `StarEmotionAccumulatedScore` entries.
- Deterministic high-confidence emotion signals now accumulate into those totals.
- The Star prompt now includes current stats plus accumulated good/bad emotion totals every turn as internal acting state.
- Hardened duplicate-key paths by grouping duplicate emotion-score keys, information availability keys, and duplicate profile IDs instead of throwing.

## Notes
- The image bytes are used by the API/host to create a safe vision summary, then the selected Star gets that summary plus her normal base profile/lore/state request.
- The model should not read relationship numbers aloud unless the player asks about mechanics.

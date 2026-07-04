# Remembered Markers / Exact Coordinates Update

Adds a durable remembered-marker pipeline for player instructions such as:

- "remember this location is at 123,456"
- "mark the hidden door at X=12 Y=88"
- "remind me later where the slime core was"

## What changed

- Added `StarRememberedMarker` model.
- Added `StarState.RememberedMarkers` so exact player-saved markers persist in `state/six-stars-local-dev-state.json`.
- Added background-review response fields:
  - `rememberedMarkers`
  - `referencedMemoryMarkerIds`
- Background prompts now instruct the Star to extract exact memory data when the player asks her to remember, mark, save, pin, note, or remind later.
- Foreground prompts now include remembered markers so the Star can answer recall questions later using exact coordinates and descriptions.
- Deterministic memory API now upserts remembered markers, emits Unity events, and stores marker history in the conversation log.
- Added a small heuristic fallback that can capture obvious remember requests with coordinates even if the local LLM fails to fill `rememberedMarkers`.
- Clear/reset Star memory clears remembered markers because they live inside `StarState`.

## Important fields

Each marker contains:

- `id`
- `markerName`
- `description`
- `locationText`
- `coordinates`
- `area`
- `sourcePlayerQuote`
- `reminderText`
- `confidence`
- `tags`

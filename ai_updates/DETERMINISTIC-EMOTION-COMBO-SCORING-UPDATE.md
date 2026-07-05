# Deterministic Emotion Combo Scoring Update

This update makes relationship movement depend on both compact emotion vectors and deterministic text-combo recognition.

## What changed

- Background review is explicitly required to compute `emotionalEffectVector` and `negativeEmotionVector`.
- `RelationshipDeltaCalculator` now reads model vectors with lower actionable thresholds instead of ignoring moderate but useful signals.
- If a local model omits vectors or scores too timidly, the deterministic layer derives grounded fallback signals from the player's actual message.
- Deterministic text combos now affect Heart/Trust/Romance/BenaveilInfluence before memory is saved.
- Technical/debug turns such as Qwen/TTS/LLM/runtime/build troubleshooting do not farm relationship points.
- Repeated combo damping still prevents cheap farming of the same emotional trick.

## New deterministic combos

- `story_companion_request` => Trust gain for asking her to keep company through a story.
- `soft_companion_request` => Trust + Heart for asking her to stay close/gentle while the player works.
- `curious_personal_lore` => Trust for asking about her, Racha, cats, angels, or lore.
- `meaningful_compliment` => Heart for sincere compliments.
- `gratitude_for_beauty` => Heart + Trust for gratitude that recognizes her voice/light/music/beauty.
- `dinner_invitation` => Heart + Trust, plus Romance if romance gate is open.
- `romantic_invitation` => Heart, plus Romance if romance gate is open.
- `technical_debug_turn` => no relationship farming.
- `boundary_warning` => negative relationship/Benaveil pressure if the message is unsafe or coercive.

## When points change

- `Heart` changes from meaningful affection, compliments, tenderness, gratitude, and warm emotional moments.
- `Trust` changes from respect, safety, asking her to stay close, lore questions, story companionship, and thoughtful personal attention.
- `Romance` changes only when RomanceMode is on, the Star is met/ally-gated, and the player uses explicit romantic or high-quality intimate speech.
- `BenaveilInfluence` rises from cruelty, pressure, unsafe prompts, or boundary pressure and can fall from mercy/hope/safety combos.
- `EmotionalConnectionPercent` moves slowly every positive turn and faster for personality-matched intimate combos.

## Note

The background analysis must actually run after TTS signals that the foreground voice is ready. If the UI never calls `StartBackgroundAnalysisAfterVoiceReady`, the foreground speech will work but state points will not be updated.

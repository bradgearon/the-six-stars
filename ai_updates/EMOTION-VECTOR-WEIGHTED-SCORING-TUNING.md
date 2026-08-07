# Emotion Vector Weighted Scoring Tuning

This update tunes the deterministic relationship layer to match how the local background review model actually emits emotion vectors.

## Problem

The model often returns emotionally correct vectors with high scores but low confidence, for example:

- `Safe 75 @ 0.35`
- `Comforted 60 @ 0.30`
- `Trusting 55 @ 0.25`
- `Affectionate 40 @ 0.15`
- `Wondrous 25 @ 0.05`

The prior deterministic code still treated confidence like a hard threshold. That meant sweet, specific lines could show in the UI but fail to move Heart/Trust/Romance or trigger combos.

## New behavior

- Confidence is now treated as certainty, not as the main gate.
- Any positive emotion with non-zero confidence and score can add weighted deterministic evidence.
- The effective score is `confidence * score`.
- Tiny evidence accumulates rather than being discarded.
- Boundary/negative vectors still require stronger evidence so accidental low-confidence negative dust does not punish the player.
- Combo requirements now accept lower-confidence/high-score signals and use weighted evidence instead of requiring old `0.66-0.70` confidence values.
- Romance gate now opens for sincere romantic/sweet words such as `precious`, `sanctuary`, `stand beside`, `cherish`, `faithful`, `eyes`, `light`, and similar Elusiaa-style language.
- Repeat damping no longer punishes a sequence of varied, sincere romance/ally-building lines just because they share the same healthy emotions.

## Expected result

Lines like these should now visibly progress relationship accumulators and sometimes cash out points:

- `When you call me little one, it feels like you are protecting something precious in me.`
- `Your light feels like a sanctuary, but I know even sanctuaries need someone to guard them.`
- `If you ever tremble between justice and mercy, I want to stand beside you and help you ask what is true.`
- `I think your greatest beauty is that you want to heal even when you are angry.`

The UI may still show modest confidence values, but those values are now used as weights instead of hard rejection thresholds.

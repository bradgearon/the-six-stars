# Emotion Vector All Nonzero Scoring

This update removes the remaining "top few emotions" behavior from deterministic positive emotion scoring.

## Why

The local background review model often returns useful emotional evidence with low confidence and a meaningful score, for example:

```text
Safe 75 @ 0.35
Wondrous 25 @ 0.05
Affectionate 40 @ 0.20
```

Confidence is a certainty multiplier, not a gate. If confidence and score are both nonzero, the entry should contribute `confidence * score` into the Star-specific relationship accumulators.

## Changes

- Positive relationship scoring now considers every nonzero positive emotion in the vector.
- Removed the remaining `.Take(8)` cap from accepted positive signal processing.
- `TopAffectedEmotions` now returns all nonzero vector entries sorted by weighted score unless a caller explicitly passes a max count.
- Desktop UI no longer trims displayed top emotions to three.
- Desktop raw vector summary no longer trims positive/negative vector descriptions to eight.
- Android top-emotion formatting no longer trims to three.
- Interstar top emotion text no longer trims to three.
- Positive vector entries are accepted for Star-specific accumulators when:
  - confidence > 0
  - score > 0
  - the Star-specific influence rule has any stat effect
- Negative/boundary vectors still require stronger evidence before they can punish state, to avoid tiny accidental negative noise.

## Expected behavior

For an Elusiaa line such as:

```text
If you ever tremble between justice and mercy, I want to stand beside you and help you ask what is true.
```

all nonzero emotions such as Safe, Comforted, Trusting, Protected, Affectionate, Wondrous, Determined, etc. can contribute to the accumulators. The UI should display the full sorted nonzero set rather than only the top three.

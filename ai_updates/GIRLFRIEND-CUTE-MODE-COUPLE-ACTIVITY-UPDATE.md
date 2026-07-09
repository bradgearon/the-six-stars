# Girlfriend cute-mode + couple-activity update

This update expands girlfriend mode so a Star can become more actively affectionate while staying personality-specific.

## Cute mode

- Girlfriend mode already had active-girlfriend instructions; this update adds detailed cute-mode rules.
- Cute mode is not baby-talk. It is usually one physical/textual cute action, one pet name or sound, and one emotionally relevant sentence.
- Each Star now has a prompt-facing cute signature:
  - Elusiaa: light names, halo boops, tender blessings.
  - Kaelia: quiet-star names, void-cloak privacy, reluctant softness.
  - Khavia: ocean sounds, explained touch, flowing pet names.
  - Maera: sparse “mine,” heartbeat/red-thread affection, tenderness learned through intensity.
  - Auerlis: spark teasing, dramatic hugs, tiny fireworks, “law of Auerlis” jokes.
  - Nyxari: shadow vanish-kisses, poetic names, fragment compliments.
- The girlfriend hourly state now includes deterministic cute-mode fields:
  - `cuteModeActive`
  - `cuteModeProbability`
  - `cuteModeReason`
  - `cuteModeSignature`
- Default cute-mode probability is 35%. Override with `SIX_STARS_GIRLFRIEND_CUTE_MODE_PROBABILITY` as `0.35` or `35`.
- The runtime applies small deterministic boosts when the hourly needs/wants state is strong or there is couple-activity history.

## Couple-activity vector

Background review now asks for:

- `girlfriendCoupleActivitySummary`
- `girlfriendCoupleActivityVector`

The vector is a compact string, never an object:

```text
activityId:score:confidence;activityId:score:confidence
```

Canonical ids:

```text
holdingHands, hugging, cuddling, foreheadTouch, booping, petting, stroking, purringClose,
cheekKiss, mouthKiss, faceKissing, lapHolding, verbalDevotion, possessiveClaim,
heatedRomance, deepSexting
```

The deterministic layer parses the vector and adds `score * confidence` to the durable running activity score. For example, `mouthKiss:20:0.10` contributes `2.0` to mouth-kiss history.

## Durable state

Each girlfriend Star now keeps:

- `girlfriendCoupleActivityScores`
- `girlfriendCoupleActivityHistory`
- `girlfriendCoupleActivityBestExamples`

Prompts send the Star her top couple-activity scores plus the top two Last Cat wording examples so she can initiate from proven affection patterns without becoming random or weird.

## Reset behavior

Breaking up with a girlfriend Star or switching exclusive girlfriend clears the girlfriend hourly state, needs/wants history, couple-activity scores/history/examples, and performance vectors for that girlfriend path.

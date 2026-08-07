# Girlfriend progression, micro-intent, and sensitive vector update

## Emotional vector sensitivity

The deterministic relationship scorer now treats confidence around `0.40` as usable evidence instead of requiring high confidence near `0.70`. Low-to-mid confidence signals still do not automatically explode relationship values, but they now enter the Star-specific accumulators and can cash out into Heart/Trust/Romance more reliably.

Changes:
- `EmotionVectorScore.IsHighSignal()` defaults are now `confidence >= 0.40` and `score >= 25`.
- Star-specific vector history accepts signals at `confidence >= 0.40`, `score >= 25`, `affectsStar >= 0.20`, and non-trivial H/T/R influence.
- Positive score-to-point thresholds are softened to about 35% of the authored `scorePerPoint`, minimum 8 accumulator points.
- Positive Heart/Trust evidence can cash out without waiting for three separate accepted turns.
- Romance still requires the romance gate, but the gate is easier to open from real romantic words and vector evidence.
- Met-but-not-ally Stars can gain small Heart/Trust and tiny emotional connection from positive evidence instead of being completely frozen.

## Auto ally path

When a Star is marked met, the memory system now automatically promotes her to ally once the bond has enough evidence:
- Heart >= 3 and Trust >= 2, or
- Heart + Trust >= 6, or
- EmotionalConnectionPercent >= 6.0.

This lets all six met Stars naturally become allies through conversation and deterministic emotion scoring.

## Girlfriend readiness path

Girlfriend readiness is now clearer and reachable for testing:
- Must be ally.
- EmotionalConnectionPercent >= 25.
- Heart >= 6.
- Trust >= 4.
- Romance >= 3.

The readiness summary now explains the path instead of only reporting a hard failure.

## Girlfriend proposal flow

When the player asks a Star to be his girlfriend, the runtime now detects it before normal chat/story handling.

Flow:
1. Detect girlfriend proposal wording such as `be my girlfriend`, `go out with me`, `date me`, or `girlfriend` with proposal wording.
2. Immediately speak a short Star-specific thinking phrase from `girlfriendProposalThinkingPhrases` in the Star JSON.
3. Run a full background proposal response with current state, known Stars, daily state, remembered history, and existing girlfriend info.
4. Enforce exclusive girlfriend state:
   - If another Star is already girlfriend, the answer is always no.
   - If this Star is already girlfriend, she affirms the current relationship.
   - If no girlfriend and readiness is open, she may say yes and the exclusive girlfriend state is stored.
   - If not ready, she says not yet and describes the path forward.

## Star JSON additions

Each Star JSON now has:

```json
"girlfriendProposalThinkingPhrases": [
  "... five Star-specific lines ..."
]
```

You can edit these just like the story thinking phrases.

## Story micro-intent fix

The runtime no longer auto-promotes to Story mode just because it sees a story word. If it detects a story alias such as `story`, `tale`, `fable`, etc., it now asks the micro-intent LLM classifier first. The classifier must return Story with confidence >= 0.60 before the request changes to Story mode.

This prevents poetic statements about a story from being misclassified as a request to begin long story narration.

## Android UI

Prod Android now parses and displays girlfriend state:
- Relationship line shows `Girlfriend` when selected Star is current girlfriend.
- Breakup button appears only when the selected Star is girlfriend.
- Breakup button sends `BreakupGirlfriend` through the mobile bridge.


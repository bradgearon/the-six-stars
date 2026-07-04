# Deterministic Emotion Pipeline + Ally State Update

This update solidifies the relationship pipeline:

1. The fast foreground LLM returns `spokenText`, `emotionalEffectVector`, and `negativeEmotionVector`.
2. The background memory review receives those foreground vectors, conversation history, emotion-update history, and the Star state.
3. The deterministic API layer combines:
   - foreground positive vector,
   - foreground negative vector,
   - background `suggestedStateDeltas`,
   - accepted combos,
   - recent repeated-update history,
   - ally status,
   - romance mode,
   - current Star state.
4. Only the deterministic output updates saved `StarState`.

The saved state now includes ally status, compact emotion-vector history, accepted high-signal emotions, accepted combo history, and the deterministic summary. Repeated positive combo farming is damped; negative/boundary repeats still matter.

Default ally state:

- Elusiaa starts as ally.
- Kaelia, Maera, Khavia, Auerlis, and Nyxari start as not allies until the host/gameplay unlocks them.

Desktop state file now defaults to:

```text
state/six-stars-local-dev-state.json
```

Override with:

```bat
set SIX_STARS_STATE_FILE=U:\somewhere\six-stars-state.json
```

The desktop UI now has a `Selected Star Is Ally` checkbox that writes the saved Star state. Unity or another host can call the runtime API to update ally status when story/gameplay progress unlocks a Star.

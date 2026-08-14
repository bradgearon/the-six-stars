# Writing review and legacy value baselines

## Canonical review discipline

All newly reviewed shelf outputs — both **Sparks** and **Constellation Stories** — use:

`reviewDiscipline: "writing-quality-score-v1"`

The final review contract is deliberately small: the reviewer reads the finished writing itself and returns **one 0-100 quality score**. It does not rate a reader-emotion vector, and it does not receive the Star personality as evidence for quality. This keeps shelf selection focused on the writing rather than on whether a line happens to trigger a particular emotional bucket.

For a five-Spark batch the JSON response is five numeric scores keyed by candidate index. A Constellation Story review returns one numeric `score`. The current persistence policy uses that finished-writing score directly. Sparks must meet the configured score threshold and at most two Sparks from one generated batch can be automatically persisted.

The strict reviewer rubric rewards originality, precise word choice, coherence, rhythm, memorability, and earned emotional/intellectual force. It penalizes strained noun/verb combinations, vague profundity, cliches, adjective stacking, mixed metaphors, and generic AI-poetic filler. Scores of 80+ are intended to mean genuinely shelf-worthy work; 90+ should be rare.

## Legacy review data

`reader-emotion-iu-v1`, `literary-values-v1`, and the short-lived `constellation-reader-emotions-v1` formats remain readable for historical files and diagnostics. The shared `writing-value-baselines.json` pack also remains available for older generation/calibration code that still references it.

Policy v3 does **not** silently reinterpret an old emotion/IU review as a writing-quality score. Existing legacy reviews stay in history and the append-only statement log, but legacy emotion-selected entries are removed from the active persisted shelf until a new quality-score review creates current shelf work. This avoids carrying forward low-quality selections merely because the old emotional calibration happened to favor them.

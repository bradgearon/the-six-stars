# Data-tuned Star-writing persistence consensus

This update retunes persistence from the fresh 1,680-statement run in `all-star-writing-statements(5).jsonl`. The prior run saved 109 statements (6.49%), but the distribution was uneven: Elusiaa saved 31 while Khavia saved only 3. It also exposed a failure mode where an inflated `Overall` score could bypass weak craft scores.

## Quality consensus gate

Each Star now has `writingPersistenceQualityGate` in her JSON profile. A candidate must:

1. Meet the Star's minimum `Overall` floor.
2. Meet at least four of seven supporting craft requirements.
3. Match at least one Star-specific persistence route, unless it passes the existing strict global Overall backstop.

The global backstop now uses the same quality gate, so `Overall` can no longer save a statement by itself. This rejects malformed or inflated vectors such as a 90 Overall paired with very low coherence, originality, meaning, and character scores.

## Data-calibrated routes

- **Elusiaa:** favors quotable emotional resonance and genuinely gentle play instead of broad hopeful-poetic overlap.
- **Kaelia:** favors haunting originality and rare high-cleverness fragments.
- **Khavia:** adds a broad but quality-gated shared-meaning route, plus stricter playful and intriguing routes.
- **Maera:** emphasizes moving, retellable blood resonance; extreme possessive depth remains possible but needs stronger consensus.
- **Auerlis:** emphasizes witty, characterful joy, with stricter spectacle and hopeful-retelling alternatives.
- **Nyxari:** emphasizes haunting cleverness and highly original aphoristic compression.

## Replay against the fresh run

Applying the new logic to the existing score vectors predicts:

| Star | Previous saved | Predicted saved | Previous rate | Predicted rate |
|---|---:|---:|---:|---:|
| Elusiaa | 31 | 19 | 10.51% | 6.44% |
| Kaelia | 15 | 20 | 5.36% | 7.14% |
| Khavia | 3 | 25 | 1.07% | 8.93% |
| Maera | 25 | 23 | 8.93% | 8.21% |
| Auerlis | 14 | 22 | 5.19% | 8.15% |
| Nyxari | 21 | 20 | 7.64% | 7.27% |
| **Total** | **109** | **129** | **6.49%** | **7.68%** |

The overall shelf remains selective while the distribution becomes substantially more balanced. The result is based on replaying the exact 27-score vectors already recorded; no additional LLM judgments were introduced.

## Compatibility

Profiles without `writingPersistenceQualityGate` retain the old behavior. Existing writing-note files need no migration because the update changes only future rule evaluation.

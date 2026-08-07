# Character attributes and decision impulse scoring

Implemented a compact character attribute layer for the Six Stars and a safe LLM scoring path for story-pressure decisions.

## Runtime behavior

- Star profiles now carry `characterAttributesVersion`, `characterAttributes`, and `characterAttributeNotes`.
- Normal foreground speech prompts include the compact attribute order and current Star scores so the model can silently flavor speech, flaws, restraint, and judgment.
- The compact attribute order is:
  `mercy,patience,justice,honor,zeal,humility,insight,hope,pride,control,wrath,fear,despair`

## Decision scoring

Added a scoring-only path that asks the LLM for soft psychology numbers, not routes or state changes.

- `StarDecisionScoreRequest`
- `StarDecisionScoreResponse`
- `IStarDecisionScorer`
- `SixStarsRuntime.ScoreDecisionAsync(...)`
- `SixStarsConversationApi.ScoreDecisionAsync(...)`
- `StructuredStarAgent.ScoreDecisionAsync(...)`

Default requested score order:
`protect,punish,investigate,reconcile,withdraw,obeyInfluence,trustPlayer,restrainSelf,escalate,seekEvidence`

The host/game layer should consume these scores deterministically and decide which dialogue route, quest flag, cutscene, or state update to apply.

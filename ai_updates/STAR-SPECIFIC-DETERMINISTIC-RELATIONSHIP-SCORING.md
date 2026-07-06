# Star-specific deterministic relationship scoring update

This update moves Heart / Trust / Romance point movement into the deterministic layer.

## What changed

- The LLM is now treated as emotional evidence only:
  - `emotionalEffectVector`
  - `negativeEmotionVector`
  - memory summaries / important quotes
- The deterministic layer now owns actual Heart, Trust, Romance, Ally, and Girlfriend progress.
- The full emotion-vector history is stored in `StarState.EmotionVectorHistory` and is not sent back into the background AI prompt.
- Durable point remainders are stored in `StarState.RelationshipScoreAccumulators` so repeated high-certainty, Star-relevant turns can add up naturally.
- Each Star JSON now includes:
  - `relationshipStats`
  - `emotionInfluence`
  - `comboRules`

## Important behavior

Positive Heart / Trust / Romance points do not cash out from one random high emotion. The scorer waits until the Star has enough relevant evidence, usually three Star-relevant emotion-vector turns or a configured combo.

Negative/boundary signals can still apply immediately so disrespect, pressure, unsafe behavior, or hurt are not damped away.

## Files touched

- `src/Wds.SixStars.Api/Engine/RelationshipDeltaCalculator.cs`
- `src/Wds.SixStars.Api/Models/StarState.cs`
- `src/Wds.SixStars.Api/Models/StarEmotionVectorHistoryEntry.cs`
- `src/Wds.SixStars.Api/Models/StarEmotionVectorHistoryItem.cs`
- `src/Wds.SixStars.Api/Models/StarRelationshipScoreAccumulator.cs`
- `src/Wds.SixStars.Api/Stars/StarRelationshipPersonalityMatrix.cs`
- `src/Wds.SixStars.Api/Stars/StarRelationshipStatsConfig.cs`
- `src/Wds.SixStars.Api/Stars/StarRelationshipStatRule.cs`
- `src/Wds.SixStars.Api/Stars/StarEmotionInfluenceRule.cs`
- `src/Wds.SixStars.Api/Stars/StarEmotionComboRule.cs`
- `src/Wds.SixStars.Api/Stars/StarEmotionComboRequirement.cs`
- `src/Wds.SixStars.Api/Content/Stars/*.json`
- `src/Wds.SixStars.Api/Ai/StarPromptBuilder.cs`

## Build note

I could not run `dotnet build` in this sandbox because the `dotnet` command is not installed here. Build locally with:

```cmd
dotnet build SixStars.sln
```

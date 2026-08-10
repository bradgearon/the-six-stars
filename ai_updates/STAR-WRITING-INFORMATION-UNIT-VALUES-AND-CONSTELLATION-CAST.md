# Open-ended writing values and collaborative Constellation cast

The writing shelf no longer treats writing review as a bounded 0-100 score. Values are open-ended **information units (IU)**. `100 IU` is the default reference point for excellent known work, not a maximum. A materially stronger Spark or Constellation Story may legitimately receive 110, 140, 200, or any other non-negative value when the evaluator judges that magnitude warranted.

Sparks retain the established 27 literary-value dimensions because their generation, ranking, and persistence rules already depend on them. Completed Constellation Stories now use the separate `constellation-reader-emotions-v1` discipline: a canonical named list of reader-emotion effects plus a separate Overall IU value. Existing score-only and older score:confidence Spark/story vectors remain readable. Persistence thresholds are named `minimumValue` / `minimumOverallValue` while retaining backward-compatible aliases for older profile JSON.

## Calibration references

`Content/Writing/writing-value-baselines.json` is an optional curated calibration pack. It is intentionally empty by default so a baseline can be selected after enough fresh material exists. A Spark baseline uses its complete text plus the legacy/current 27 literary IU values. A new Constellation baseline uses its complete story, `reviewDiscipline: "constellation-reader-emotions-v1"`, every canonical named `emotionFeedback` value, and a separate `overallValue`.

If curated references are missing or insufficient, `StructuredStarAgent` fills the remaining reference slots from the deterministically highest-valued compatible works already persisted on that Star's shelf. Spark creation, improvement, and review use up to five valued Spark references. Constellation outline/build/final generation can learn from the strongest compatible complete story references. Constellation review itself only calibrates against stories reviewed under the same reader-emotion discipline, so an old literary vector cannot silently masquerade as reader-emotion feedback.

References calibrate magnitude and quality only. Prompts explicitly tell the Stars not to copy their plots, wording, metaphors, titles, casts, or endings.

## Collaborative cast and story subjects

Constellation outlines now return:

- `characters`: the living named cast, with each string free to include role, desire, fear, contradiction, relationship, voice, or memorable detail;
- `storySubjects`: the living list of what the story is actually about — situations, relationships, mysteries, conflicts, themes, and ideas;
- `beats`: the ordered structural movements.

Every build phase receives those lists and returns the complete updated cast and story-subject list alongside the revised beats and full story. The persisted development trace records the cast and story subjects at each outline/build/final stage.

The prompts encourage stories to be about anyone: a Star, several Stars, The Last Cat, entirely new characters, creatures, communities, or other casts. Recurring characters should be named and memorable instead of remaining scaffolding such as `the subject` or `the central figure`. Builders are gently encouraged to use a broad dramatic palette when the story calls for it: battles and confrontation, action, romance/desire, worry/dread, humor/play, tenderness, friendship/rivalry, mystery/discovery, danger, sacrifice, awe, horror, wonder, reversals, choices, quiet emotional beats, and consequences. None of these are hard requirements or count targets.

## Last Cat naming rule

Outline, build, and final-revision prompts now use one consistent rule. The Last Cat may absolutely be a real story character, but the model must establish and name him as **The Last Cat** in narration instead of silently converting an unnamed reader or generic protagonist into him. Companion pet names such as `little one` are allowed only as intentional dialogue after the story itself establishes the speaker, addressee, and relationship; they are not generic reader address or substitutes for a character name.

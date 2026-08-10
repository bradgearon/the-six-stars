# Constellation Forge reader-emotion IU review

Completed Constellation Stories no longer reuse the Spark-oriented 27 literary-category vector. Forge review now uses a named, disciplined reader-emotion list plus a separate open-ended Overall IU value.

The review discipline id is `constellation-reader-emotions-v1`. Every review must return every canonical emotion exactly once:

`Mirthful, Playful, Delighted, Intrigued, Engrossed, Surprised, Wondrous, Awed, Admiring, Moved, Tender, Comforted, Hopeful, Inspired, Impassioned, Exhilarated, Connected, Affectionate, Attracted, Yearning, Vulnerable, Melancholy, Sad, Haunted, Uneasy, Afraid, Concerned, Angry, Determined, Contemplative`.

`Mirthful` is deliberately the humor-response bucket. It means the story actually produced mirth / the impulse to laugh, from a genuine smile or chuckle through full laughter. The IU magnitude carries the strength of that response, so the category name does not need to collapse weak amusement and powerful laughter into one fixed intensity. It is not a generic quality score and it replaces the temptation to call every good work `Funny` or `Amused`.

All emotion values and `overallValue` are non-negative information units with no upper bound. High negative-valence effects such as Haunted, Uneasy, Afraid, Sad, or Angry are not failures: they can be evidence that horror, tragedy, danger, or moral conflict landed strongly.

## Persistence

Sparks keep their existing literary-value persistence rules. Constellations now have separate `constellationPersistenceRules` and `constellationPersistenceQualityGate` entries in each Star profile. The old routes were translated by intent into reader effects so the shelf continues catching the work each Star is meant to preserve: for example Elusiaa's radiant resonance is now Moved + Tender, Auerlis's bright spectacle is Exhilarated + Surprised, Kaelia's fatal beauty is Haunted + Wondrous, Khavia's shared meaning is Moved + Connected, Maera's possessive depth is Yearning + Haunted, and Nyxari's fragmentary metaphysical route is Haunted + Contemplative.

The global Overall excellence backstop still applies after the Constellation impact-consensus gate.

## Storage and baselines

New Constellation shelf objects record `reviewDiscipline`, `emotionFeedback`, and `overallValue`. `EmotionalVector` is left empty for this discipline so named emotion feedback cannot be accidentally reinterpreted as the old positional literary vector. Statement-log records are version 5 and include the named list plus a `Values` dictionary for inspection.

Curated `writing-value-baselines.json` examples can now use the same review discipline, full story, complete named `emotionFeedback`, and `overallValue`. The story-review prompt only compares against same-discipline baselines. If none exist yet, it bootstraps from the 100 IU excellence reference. Once same-discipline stories accumulate on the shelf, the best of those can fill missing calibration slots automatically.

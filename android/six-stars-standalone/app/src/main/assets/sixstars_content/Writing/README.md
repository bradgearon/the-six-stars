# Writing value baselines

`writing-value-baselines.json` is the shared calibration pack for the writing shelf. Reader-emotion IU review must use the same curated ruler for every actor; it must never learn its numeric scale from the current actor's persisted shelf.

The value system is open-ended and measured in **information units (IU)**: values are non-negative and have no maximum. `referenceValue` defaults to `100` IU; it is a reference point for excellent known work, never a ceiling.

## Canonical review discipline

All newly reviewed shelf outputs — both **Sparks** and **Constellation Stories** — use:

`reviewDiscipline: "reader-emotion-iu-v1"`

The LLM contract is intentionally compact for local-model speed. A review is one positional semicolon-delimited `valueVector`, not a JSON array of per-emotion objects.

The vector contains the 30 canonical reader-emotion IU buckets in fixed order followed by **Overall** as the final value. Overall is integrated literary/information value; it is stored in the vector so Spark and Constellation reviews use exactly the same contract.

A curated example therefore looks like:

```json
{
  "id": "kaelia-reader-emotion-baseline-1",
  "starId": "Kaelia",
  "outputType": "constellation-story",
  "reviewDiscipline": "reader-emotion-iu-v1",
  "title": "Example title",
  "writing": "Full untruncated work...",
  "valueVector": "4;2;18;128;121;...;137;132"
}
```

The abbreviated vector above only demonstrates the storage shape; a real baseline must contain every canonical position. Shared calibration examples should normally leave `starId` blank so they cannot be interpreted as actor-specific standards. The authored file order is preserved intentionally so the pack can include low, medium, strong, and excellent anchors rather than only the highest-valued examples.

Older `literary-values-v1` 27-value Spark vectors remain readable for backward compatibility. The short-lived `constellation-reader-emotions-v1` named-array format is also migrated on load into the compact ReaderEmotionIuV1 vector. New LLM requests never ask for the named-array format.

Reader-emotion review never fills missing calibration slots from an actor's persisted shelf. If the curated shared pack is absent or incomplete, the fixed system-level IU ladder is the fallback ruler. Legacy literary vectors remain readable but are not silently relabeled as reader emotions.

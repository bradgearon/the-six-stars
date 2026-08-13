# Writing value baselines

`writing-value-baselines.json` is an optional curated calibration pack for the writing shelf.

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

The abbreviated vector above only demonstrates the storage shape; a real baseline must contain every canonical position.

Older `literary-values-v1` 27-value Spark vectors remain readable for backward compatibility. The short-lived `constellation-reader-emotions-v1` named-array format is also migrated on load into the compact ReaderEmotionIuV1 vector. New LLM requests never ask for the named-array format.

When curated examples are absent or do not fill the requested reference set, the app fills remaining slots from the highest-valued compatible works already persisted on that Star's shelf **under the same ReaderEmotionIuV1 discipline**. Legacy literary vectors are not silently relabeled as reader emotions.

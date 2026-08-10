# Writing value baselines

`writing-value-baselines.json` is an optional curated calibration pack for the writing shelf.

The value system is open-ended and measured in **information units (IU)**: values are non-negative and have no maximum. `referenceValue` defaults to `100` IU; it is a reference point for excellent known work, never a ceiling.

Baselines are review-discipline aware.

## Sparks / legacy literary values

Spark examples use `reviewDiscipline: "literary-values-v1"` (or omit it for backward compatibility) and may provide either the compact 27-value `valueVector` or the named `values` object already used by the Spark review system.

## Constellation Stories / reader-emotion values

New Constellation Story reviews use `reviewDiscipline: "constellation-reader-emotions-v1"`. Their emotional feedback is a **named list**, not a positional vector, and `overallValue` is stored separately because Overall is not an emotion.

Example:

```json
{
  "id": "kaelia-emotion-baseline-1",
  "starId": "Kaelia",
  "outputType": "constellation-story",
  "reviewDiscipline": "constellation-reader-emotions-v1",
  "title": "Example title",
  "writing": "Full untruncated work...",
  "overallValue": 132,
  "emotionFeedback": [
    { "emotion": "Mirthful", "value": 8 },
    { "emotion": "Playful", "value": 12 },
    { "emotion": "Delighted", "value": 28 },
    { "emotion": "Intrigued", "value": 121 }
  ]
}
```

A real Constellation baseline must contain **every canonical reader-emotion bucket exactly once**. The abbreviated list above only demonstrates the JSON shape.

When curated examples are absent or do not fill the requested reference set, the app fills remaining slots from the highest-valued compatible works already persisted on that Star's shelf. For Constellation review calibration, only stories already reviewed under the same named reader-emotion discipline are used; older literary-vector stories are not silently treated as if their old categories were emotions.

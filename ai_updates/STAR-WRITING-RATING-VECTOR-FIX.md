# Star Writing Rating Vector Fix

## Symptom

Writing notes were saved with vectors such as:

```text
10:0.8;0:0;0:0;...;0:0
```

Only the first category contained a rating, so no writing could satisfy the Star's persistence rules.

## Root cause

The local model returned several noncanonical review shapes:

1. A compact object keyed by candidate index:

```json
{"0":"...", "1":"...", "2":"...", "3":"...", "4":"..."}
```

2. A `ratings` array whose items used `starId` as the numeric candidate index instead of `index`.

3. Emotional vectors that used colons between every number instead of semicolons between `score:confidence` pairs.

The old code deserialized only the canonical `starId + ratings[index, emotionalVector]` shape. Missing indexes defaulted to zero, and `StarWritingEmotionVector.Normalize` split only on semicolons. A malformed colon chain was therefore interpreted as one pair and padded with 26 zero pairs.

## Fix

- The preferred response is now the simplest shape the local model already produced reliably:

```json
{"0":"vector", "1":"vector", "2":"vector", "3":"vector", "4":"vector"}
```

- The Scriban prompts state that every value must contain exactly 27 colons and 26 semicolons.
- The response schema now requires only the five numeric keys.
- The parser remains compatible with:
  - the new numeric-key object;
  - the former canonical `ratings` array;
  - legacy rating items that use numeric `starId` as the index;
  - a root array or a `ratings` object keyed by index.
- Complete vectors are validated before persistence. The validator accepts:
  - 27 semicolon-delimited `score:confidence` pairs; or
  - 54 colon-delimited alternating score/confidence numbers when all data is present.
- If the first rating output is malformed, one small Scriban repair request is made.
- If the repair output is still malformed, the operation fails and no new writing batch is saved. The system no longer silently stores padded zero vectors.

## Existing broken notes

On the next **Create 5 Jokes / Poems** operation, recent writings with zero confidence in nearly every category are detected and re-rated in groups of five. Their persistence rules are then recalculated, and qualifying writings are added to the pending shelf. The desktop and mobile status message reports how many older ratings were repaired.

The persisted JSON continues to store only the compact `EmotionalVector` string. Expanded emotional-value objects are never written.

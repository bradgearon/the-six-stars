# Star Writing Rating Array Hardening

## What the debug log revealed

The old local-model response repeatedly used a numeric `starId` inside each rating object. Those values were actually the zero-based writing positions `0` through `4`, not Star identifiers.

The same responses frequently returned only 26 to 28 colon-separated numbers. That represents only 13 or 14 score/confidence pairs, not the required 27 categories. One response returned 27 semicolon-delimited pairs but used the category positions `0` through `26` as the score values.

These are model-format failures, not output-token truncation: the responses ended with `finish_reason: stop` and remained below the configured completion-token limit.

## Hardened response contract

The evaluator now returns this outer shape:

```json
{
  "0": ["score:confidence", "score:confidence"],
  "1": ["score:confidence", "score:confidence"],
  "2": ["score:confidence", "score:confidence"],
  "3": ["score:confidence", "score:confidence"],
  "4": ["score:confidence", "score:confidence"]
}
```

Every array must contain exactly 27 strings. The API joins those strings into the compact semicolon-delimited vector before persistence, so writing-notes files still store only one `EmotionalVector` string.

The parser remains backward-compatible with earlier string vectors and the legacy numeric `starId` mistake, but new prompts no longer request either shape.

## Additional validation

The API rejects and repairs a result when:

- a candidate does not contain exactly 27 pairs;
- too many confidences are empty;
- the model uses category positions `0..26` as score values;
- almost every score is in the `0..1` confidence range, suggesting reversed or placeholder output.

If the repair request also fails, no new writing batch is saved.

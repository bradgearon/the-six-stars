# Interstar Relationship Vectors Update

This update adds a durable one-way relationship layer between Stars and expands the interstar ripple prompts so a relay can affect both the Last Cat relationship and Star-to-Star relationships.

## State

- `StarState.OtherStars` stores one entry per other Star.
- Each entry is prompt-facing in the compact format:
  `StarName:isMet:isAlly:isGirlfriend:trustScore:heartScore`
- Each entry also stores the last positive/negative interstar emotion vectors, update time, and a bounded history of applied Star-to-Star Heart/Trust deltas.
- State stores normalize the new list on load/default creation.

## Prompts

- Normal speech, daily state, girlfriend hourly state, and interstar prompts now include the `Other stars:` block.
- Interstar reaction prompts now ask for:
  - Last Cat positive/negative vectors.
  - Source-Star positive/negative vectors.
  - `interstarEmotionVectors` with one item for the source Star.
- Interstar feedback prompts now ask the origin Star for one `interstarEmotionVectors` item per responding Star.
- Prompt wording keeps Last Cat pronouns clear: the Last Cat is he/him/his; `you` refers to the Star being addressed.

## Deterministic interstar scoring

- Interstar vector evidence applies to durable one-way Star-to-Star Heart/Trust scores.
- All-zero Star-to-Star vectors are recorded as no-op and do not move meters.
- Debug logs now include interstar vectors, applied interstar relationship deltas, and the `OtherStars` shard in state snapshots.

# Unified reader-emotion IU review

Spark and Constellation shelf reviews now use the same `reader-emotion-iu-v1` discipline. The review is intentionally one compact positional string rather than a JSON array of named emotion objects, because the local model handles the compact contract materially faster.

The vector contains 31 open-ended IU values: 30 canonical reader-emotion buckets followed by `Overall`. The exact order is supplied in every review prompt. Example shape: `4;2;18;128;121;...;137;132`. Values are information units, not percentages or bounded scores, and can exceed 100.

The 30 reader effects are: Mirthful, Playful, Delighted, Intrigued, Engrossed, Surprised, Wondrous, Awed, Admiring, Moved, Tender, Comforted, Hopeful, Inspired, Impassioned, Exhilarated, Connected, Affectionate, Attracted, Yearning, Vulnerable, Melancholy, Sad, Haunted, Uneasy, Afraid, Concerned, Angry, Determined, Contemplative. `Overall` is position 31.

Mirthful remains the humor-response bucket: actual mirth or impulse to laugh, not simply recognition that a joke was attempted. Negative-feeling buckets such as Haunted, Uneasy, Afraid, Angry, or Sad are reader effects and may be high in excellent work.

Both output types use the same parser, baseline discipline, persistence category vocabulary, deterministic ranking support, and storage field (`EmotionalVector`). `OverallValue` is cached from the final vector position for convenient querying. New reviews never request the former named `emotionFeedback` array. Older Constellation named-array data is converted to the compact vector on load; old `literary-values-v1` data stays readable as legacy data rather than being semantically relabeled.

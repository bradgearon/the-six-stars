# Two-stage Star writing: word selection before composition

Star writing generation now separates spontaneous word choice from the creative task.

## Stage 1: private word selection

The first LLM request asks the selected Star for exactly five pairs of two words. It receives compact identity, personality, behavior, private/canon context, and the persisted historical used-word list.

The prompt does **not** mention jokes, poetry, aphorisms, writing, punchlines, or the later composition request. It asks only for five varied pairs of substantially unrelated words and returns:

```json
{
  "starId": "Kaelia",
  "wordPairs": [
    ["bellows", "moth"],
    ["syntax", "compass"],
    ["marrow", "cadence"],
    ["tide", "sprocket"],
    ["obsidian", "dew"]
  ]
}
```

The response is validated as exactly five two-word pairs with no repeated word across the ten selections.

## Stage 2: composition from fixed pairs

A second LLM request receives the complete creative-writing character card plus the five preselected pairs. It must preserve their order and write one finished joke, aphorism, tiny poem, or poetic fragment for each pair.

The composition prompt tells the Star to privately consider several possible connections, tensions, reversals, images, implications, or punchlines for each pair before choosing the strongest idea. It must not reveal that brainstorming.

The response contains only `starId` and five finished strings. The application attaches the exact Stage 1 pairs to those strings before the existing review, persistence, history, and all-Star statement-log pipeline runs.

## Request sequence

A normal generation cycle is now:

1. `star-writing-word-pairs`
2. `star-writing-compose`
3. zero or more `star-writing-improve-pass-N` requests selected by the configured writing-pass range and weights
4. `star-writing-rate`
5. optional `star-writing-rate-repair` only when the score-vector response is malformed

The word-pair and composition requests together are creative pass 1. See `STAR-WRITING-ITERATIVE-PASSES.md` for the reusable pre-review passes and configuration.

All writing requests continue to omit `max_tokens`; their output is bounded by JSON schema and parser validation instead.

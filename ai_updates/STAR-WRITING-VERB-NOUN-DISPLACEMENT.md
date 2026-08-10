# Star Writing Methods: Semantic Collision + Verb and Noun Displacement

The writing bench now chooses one creative method at the start of every batch.

## Weighted method selection

```json
"WritingMethods": {
  "SemanticCollisionWeight": 0.7,
  "VerbNounDisplacementWeight": 0.3
}
```

The values are relative weights. With the defaults, approximately 70% of batches use semantic collision and 30% use verb and noun displacement. Either weight can be set to zero to disable that method without changing code.

Environment overrides:

- `SIX_STARS_WRITING_SEMANTIC_COLLISION_WEIGHT`
- `SIX_STARS_WRITING_VERB_NOUN_DISPLACEMENT_WEIGHT`

## Semantic collision

This is the existing process:

1. Privately select five pairs of substantially unrelated words.
2. Write one concise finished piece from each pair.
3. Run the configured number of review/improvement passes.
4. Run the normal 27-score persistence review.

## Verb and noun displacement

Pass one is also two staged:

1. The Star privately thinks of five objects and exactly three ordinary actions for each object.
2. For each object, she chooses one normal action, replaces it with an unexpected verb/action, and completes the resulting statement into something beautiful, deep, funny, interesting, exciting, or otherwise memorable.

The second response records the noun, the ordinary verb that was displaced, the new displaced verb, and the finished writing. Internally the candidate's two tracked words are the noun plus displaced verb so it can use the existing review, history, scoring, and persistence pipeline.

Passes two and later use a displacement-specific review prompt. A proposed replacement may change the noun, the displaced verb, or both, then rewrite the completion. It returns the same old/new 0-100 scores as semantic collision. The runtime accepts only a strictly higher-scoring replacement.

The same 100 optional seed words are available to the displacement setup, composition, and later review passes.

## Persistence and diagnostics

Writing notes are version 8. New batches, history items, persisted writings, and the all-Star JSONL statement log record `WritingMethod`, allowing quality and persistence comparisons between:

- `semantic-collision`
- `verb-noun-displacement`

The latest notes state also records `LatestWritingMethod`.

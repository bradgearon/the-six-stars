# Iterative Star writing passes

The writing shelf now treats its creative workflow as a configurable sequence of passes.

## Pass 1: selected creative method

The initial pass first chooses a creative method by weight. `semantic-collision` uses the established hidden five-pair selection followed by composition. `verb-noun-displacement` privately chooses five objects and three ordinary actions for each, then replaces one ordinary action with an unexpected verb and completes the statement.

Both methods produce five normalized candidates and the runtime assigns stable ids `candidate-1` through `candidate-5`.

## Passes 2 and later: reusable literary review

Every additional pass calls `star-writing-improve-pass-N` with the current candidate ids, current word sets, and current writings. The Star returns proposed replacements with a 0-100 score for both the current version and the proposed version:

```json
[
  {
    "id": "candidate-2",
    "oldScore": 78,
    "newWordSet": ["sextant", "shadow"],
    "newWriting": "Even the most precise sextant cannot map the shadow of a forgotten bell.",
    "newScore": 91
  }
]
```

A replacement may preserve both words, replace one word, or replace both words. The runtime applies it only when `newScore` is strictly greater than `oldScore`; ties and lower scores keep the current item. Omitted ids also remain unchanged. Every accepted replacement immediately updates the working set, so the next pass receives the newest words and writing rather than the original draft.

After every completed pass, the per-Star writing-notes state is saved with:

- `LatestSelectedPassCount`
- `LatestCompletedPassCount`
- `LatestAppliedImprovementCount`
- `LatestPassUpdatedAt`
- `LatestWritingPasses`, containing a snapshot of all five ids, word sets, and writings for each pass

The normal compact 27-score `star-writing-rate` request still runs only once, after all selected creative passes are complete. Persistence rules therefore judge the final version.

## Weighted pass selection

Before each writing-shelf run, the service selects a total pass count from the configured range. With the defaults:

- 50% selects one total pass;
- 30% selects either two or three total passes;
- 20% selects four through six total passes.

Weights are normalized against the configured range, so ranges such as `2..4` or `4..8` still work. Passes above six are supported when `MaximumPasses` is increased because the additional-pass template is reusable.

## Configuration

```json
"WritingPasses": {
  "Enabled": true,
  "SeedWordsEnabled": true,
  "MinimumPasses": 1,
  "MaximumPasses": 6,
  "OnePassWeight": 0.5,
  "TwoToThreePassWeight": 0.3,
  "FourOrMorePassWeight": 0.2
}
```

`Enabled: false` disables additional review/rewrite passes while preserving the selected method's mandatory first pass. Setting both `MinimumPasses` and `MaximumPasses` to `1` also forces exactly one pass.

Environment overrides are available:

- `SIX_STARS_WRITING_PASSES_ENABLED`
- `SIX_STARS_WRITING_SEED_WORDS_ENABLED`
- `SIX_STARS_WRITING_MIN_PASSES`
- `SIX_STARS_WRITING_MAX_PASSES`
- `SIX_STARS_WRITING_ONE_PASS_WEIGHT`
- `SIX_STARS_WRITING_TWO_TO_THREE_PASS_WEIGHT`
- `SIX_STARS_WRITING_FOUR_OR_MORE_PASS_WEIGHT`

## Word-pair repetition memory

Before the private `star-writing-word-pairs` request, each Star now receives:

- her ten newest complete five-pair selections;
- the ten most frequently selected words and each word's cumulative use count;
- a short instruction not to reuse the same word across multiple pairs very often.

The per-Star writing-notes state stores this in `RecentGeneratedWordSets` and
`GeneratedWordUsageCounts`. Counts are recorded from the initial hidden word-pair
selection request; later rewrite-pass substitutions do not retroactively alter that
history. Existing Version 5 notes files migrate to Version 6 and seed the new fields
from available recent writing history.

## Optional random seed words

When `SeedWordsEnabled` is true, the desktop host creates one array of 100 optional word ideas at the start of the writing cycle. The selected creative method receives the same array during its setup/composition flow and later improvement passes. The prompt only tells the Star that she may use the words for ideas; it does not require any seed to appear in the result.

The generator uses relative weights of `0.50` noun, `0.25` adjective, and `0.40` verb. Because those values total `1.15`, they are normalized during random selection.

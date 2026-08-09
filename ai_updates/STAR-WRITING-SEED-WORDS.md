# Star writing seed words

Each writing cycle can now receive one optional array of 100 random seed words. The same array is included in the initial private word-pair selection request and in every later improvement pass, so a Star can use it for fresh vocabulary without being required to copy any seed.

The desktop host generates the words with `CrypticWizard.RandomWordGenerator` 0.9.5. For each slot it selects a part of speech by relative weight:

- noun: `0.50`
- adjective: `0.25`
- verb: `0.40`

The weights are normalized before selection. Duplicate words are avoided while the list is assembled, and the completed list is shuffled before it is sent to the writing agent.

Enable or disable the feature in the existing writing configuration:

```json
"WritingPasses": {
  "Enabled": true,
  "SeedWordsEnabled": true
}
```

The environment override is `SIX_STARS_WRITING_SEED_WORDS_ENABLED`.

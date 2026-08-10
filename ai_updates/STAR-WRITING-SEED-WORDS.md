# Star writing seed words

Writing cycles can receive an optional array of 100 random seed words from `CrypticWizard.RandomWordGenerator` 0.9.5.

## Semantic collision

Semantic collision keeps the original mixed seed vocabulary. For each slot the provider selects a part of speech by relative weight:

- noun: `0.50`
- adjective: `0.25`
- verb: `0.40`

The weights are normalized before selection. Duplicate seed words are avoided and the completed 100-word list is shuffled.

## Verb/noun displacement

Verb/noun displacement now requests a separate seed list of **100 nouns only** from the same provider. This list is used as optional inspiration for the initial object/noun selection and as optional noun inspiration in later improvement passes. It is not presented as a replacement-verb list.

The displacement noun-selection prompt also receives the most recent 50 selected nouns and the top 100 selected nouns with cumulative use counts, so seed variety is combined with explicit repetition avoidance.

Enable or disable seed generation in the existing writing configuration:

```json
"WritingPasses": {
  "Enabled": true,
  "SeedWordsEnabled": true
}
```

The environment override is `SIX_STARS_WRITING_SEED_WORDS_ENABLED`.

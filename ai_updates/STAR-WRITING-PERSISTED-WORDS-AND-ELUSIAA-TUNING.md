# Persisted-only words and Elusiaa persistence tuning

## Used-word behavior

A generated word is now added to `AllUsedWords` only when its matching writing is persisted to the writing shelf.

- Rejected writings remain in the ten-item debug/history list, but their words stay available for future generations.
- The generation prompt now receives only the ten most recent shelf-persisted writings as continuity examples.
- `AllUsedWords` is rebuilt from `PersistedWritings` whenever notes are loaded or saved, migrating older Version 1-3 note files automatically.
- Writing-note state is now Version 4.

## Elusiaa persistence rules

Elusiaa was under-saving because her previous routes required either strong hope plus meaning, high beauty plus movement, or overt humor plus playfulness. Good aphoristic mercy lines often scored highly for poetry and quotability without crossing those narrow pairs.

Her routes now are:

- `radiant-meaning`: Hopeful 68 + Meaningful 72
- `beautiful-mercy-fragment`: Beautiful 76 + Moving 64
- `luminous-aphorism`: Poetic 76 + Quotable 70
- `gentle-play`: Funny 72 + Playful 70

The new `luminous-aphorism` route is intended to catch lines such as the stained-glass/blueprint, prism/apricot, quarry/shale, and shepherd/chime pieces without making ordinary low-poetry fragments persist automatically.

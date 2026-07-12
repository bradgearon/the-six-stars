# Star stories, jokes, player learning, and secret-canon update

## Foreground behavior

- Every Star now fulfills open-ended story requests in character with a complete arc and the player-requested genre, subject, mood, viewpoint, intensity, and length.
- Joke requests now require exactly the requested number of distinct, character-fitting jokes.
- Low Heart/Trust may reduce intimacy or autobiographical detail, but cannot justify evasion, undercounting, or an unfinished story.
- The shared cave canon now treats the cave network as dark, deep, fast-moving, and lethal. Cats and Stars are vulnerable, the Last Cat needs Star powers to survive, and he is the only present hope of restoring the cats.

## Accurate game-information follow-up

- Questions about skills/spells/abilities, monsters/bosses/creatures, and people/NPCs/characters must use supplied canon only.
- The foreground model and a deterministic category check populate `informationNeeds` with `lore`, `bestiary`, or `characters` rather than inventing missing details.
- The existing deterministic deep-information pass then answers with available local canon or admits that the exact information was not found.

## Durable things learned from the player

- Foreground Chat and Story responses now include `thingsLearnedOrSuggestedByPlayer: string[]`.
- New explicit player facts, reported events, preferences, plans, and attributed game-world suggestions are deduplicated and stored per Star in `StarState`.
- The durable list is included in future foreground prompts, daily state generation, and girlfriend-hourly state generation.
- Newly persisted items are written to the conversation log and emit a `StarLearnedAboutPlayer` Unity event.

## Hidden endgame canon

- Removed the hidden endgame name, parent-form fields, parent-form people entries, and origin-form names from all six Star JSON profiles, fallback profiles, speech/review prompts, daily-state model context, and ASR vocabulary.
- The internal branching metric remains internal for save compatibility, but is no longer exposed to Star model requests or response schemas.

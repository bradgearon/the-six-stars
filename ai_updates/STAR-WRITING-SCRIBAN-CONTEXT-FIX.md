# Star Writing Scriban / Context-Size Fix

## Failure addressed

A second **Create 5 Jokes / Poems** run could exceed a 16,384-token model context because the generation request embedded:

- the entire Star profile as indented JSON;
- the last ten writing records as indented JSON;
- a 27-category expanded `EmotionalValues` object for every history item;
- unrelated romance, relationship, inter-Star, story-deck, and voice-clone configuration.

That made the second generation prompt grow beyond the endpoint context before the model could answer.

## Changes

1. All three writing request types now use dedicated Scriban system/user templates.
2. The generation card includes only writing-relevant character identity, personality, background, voice, attributes, imagery, quotes, behaviors, storytelling instincts, selected secrets, and canon notes.
3. Relationship mode, girlfriend mode, flirt/cute instructions, relationship scoring, emotion matrices, combo rules, inter-Star rules, people/contact lists, story decks, and clone-audio configuration are excluded.
4. The last ten writings are rendered as compact lines containing date, word pair, and writing only.
5. The complete used-word history remains included, but as one compact word list rather than serialized state JSON.
6. The review request receives only a compact evaluator card and the five candidates.
7. The performance request receives the complete persisted collection in a compact human-readable format, with each rating represented by its vector string.
8. `EmotionalValues` has been removed from writing history and persisted-writing models. Only `EmotionalVector` is saved.
9. Persistence thresholds and voice-emotion selection parse the vector into an in-memory dictionary only when needed.
10. Writing-notes schema version is now `2`. Legacy `EmotionalValues` fields are ignored on load and disappear on the next save.

## New templates

- `Content/PromptTemplates/star_writing_generate_system.sbn`
- `Content/PromptTemplates/star_writing_generate_user.sbn`
- `Content/PromptTemplates/star_writing_review_system.sbn`
- `Content/PromptTemplates/star_writing_review_user.sbn`
- `Content/PromptTemplates/star_writing_performance_system.sbn`
- `Content/PromptTemplates/star_writing_performance_user.sbn`

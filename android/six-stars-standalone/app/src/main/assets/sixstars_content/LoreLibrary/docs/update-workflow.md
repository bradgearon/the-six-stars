# Update Workflow

This library is designed to be updated as new canon is given.

## Default update process

1. **Identify the entry type.** Is the new material lore, character, enemy, plant, letter, area design, system, quest, dialogue, art reference, or production rule?
2. **Update the authoring source first.** For example, a new Maera secret can be drafted in the relevant character/source document.
3. **Update the canonical runtime index.** Content-backed retrieval reads one complete index from `docs/lore/`, `docs/bestiary/`, `docs/characters/`, `docs/world/`, or `docs/story/`.
4. **Update the game bible if canon changed.** Core story, tone, mechanics, areas, endings, or companion rules must be reflected in `docs/game-bible/game-developer-bible.md`.
5. **Update cross-system docs.** Romance, AI dialogue, skill trees, endings, audio motifs, or MMO carry-over may need changes.
6. **Log the change.** Add a short note to `CHANGELOG.md`.
7. **Export a new zip.** The current deliverable should always be the entire library as a zip.

## Filing rules

- **Letters to Leviathan or Behemoth/Bahamut:** `docs/letters/guardians/`.
- **Letters carried by the Last Cat from childhood:** `docs/letters/last-cat/`.
- **Letters from settlements, mothers, fathers, students, villagers:** keep the raw letter in the proper folder and link it from the index.
- **Enemies:** update `docs/bestiary/index.md`; it is the one complete bestiary file sent during a bestiary follow-up.
- **Plants and flora:** `docs/plant-life/` even if they are dangerous.
- **Stars:** character entry, AI profile, relationship notes, skill tree links, and game bible summary.
- **Area art descriptions:** update `docs/areas/index.md`, then refresh `docs/world/index.md` because world retrieval sends the complete world index.

## Canon tiers

- **Canon:** curated docs in `docs/` outside `source-imports`.
- **Source archive:** `docs/source-imports/` files converted from original documents and conversations.
- **Draft:** any section labeled `Draft`, `Open Question`, or `Needs Design`.

## Grok Import Handling Rule

When importing a long AI conversation export:

1. Preserve the raw transcript under `docs/source-imports/`.
2. Preserve user-only input separately.
3. Create a keyworded index for navigation.
4. Copy any images into `assets/source-imports/` and create a contact sheet.
5. Promote only stable design decisions into canonical docs.
6. Put speculative, private, spiritual, or spoiler-heavy material in private/dev-note files.
7. Update the game bible, system docs, character docs, area docs, bestiary/plant-life docs, and letters folders as needed.
8. Add an entry to `CHANGELOG.md` and `data/import_manifest.json`.

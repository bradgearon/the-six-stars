# Lore Library Runtime Index

The runtime sends exactly one canonical `index.md` for a content-backed information need.

| informationNeeds dataType | Folder sent | Contents |
|---|---|---|
| `lore` | `lore/index.md` | Skills, spells, abilities, weapons, combat, and companion mechanics |
| `bestiary` | `bestiary/index.md` | Monsters, enemies, creatures, bosses, HP, attacks, and drops |
| `characters` | `characters/index.md` | Stars, people, NPCs, companions, guardians, and settlements |
| `world` | `world/index.md` | Areas, locations, ecology, traversal, settlements, and quests |
| `story` | `story/index.md` | Canon scenes, dialogue, meetings, and remembered in-world stories |

`history`, `quotes`, `markers`, and `screen` are dynamic state sources and do not map to lore folders. The `source-imports` folder is an authoring archive and is never sent to a Star.

The detailed `areas` authoring material has been consolidated into `areas/index.md`; the runtime-facing `world/index.md` includes that complete area index.

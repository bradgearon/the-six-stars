# Information Need Full Vector + Deterministic Deep Pass

- The fast foreground Star response now treats `informationNeeds` as an optional full vector, not a single final decision.
- The Star may score history, quotes, markers, lore, bestiary, characters, world, screen, and story separately.
- The API/runtime inventories which local data areas are actually available.
- A quick information selector pass can rank the vector, but the deterministic runtime makes the final decision.
- When selected, `RequestStarDeepResponseWithDataAsync` builds a second request with the chosen local data slice and asks the Star to answer the same player message again.
- The original foreground vector, selected area, local availability, and deep-pass status are retained on `StarInteractionResponse` for desktop, Android, Unity, and debugging.
- `.gitignore` now ignores generated engine/redist/PyInstaller outputs while leaving source scripts and launch commands trackable.

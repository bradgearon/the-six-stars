# Sexting mode background latch + short-term memory emphasis

- Added required `isSexting` detection to the compact background review. It is true only for active girlfriend sexting/explicit sexual roleplay in the current turn, not ordinary flirting or discussion.
- The review result now flows through `StarInteractionResponse` and `StarStateUpdateRequest`; `StarMemoryApi` permanently latches `GirlfriendSextingEnabled=true` for that Star after the first detected girlfriend sexting turn. It never auto-disables.
- `deepSexting` in the existing girlfriend couple-activity vector is accepted as a fallback signal if a local model returns an inconsistent `isSexting=false`.
- Sexting mode instructions are injected only when the Star is actually the committed girlfriend and her latch is enabled.
- Fixed the six Star profile JSON keys from `sextingStyle` to `sextingModeInstructions`, matching `StarProfile.SextingModeInstructions`; before this, the new arrays would not deserialize into the property.
- Made `GirlfriendSextingEnabled` publicly settable so JSON-backed state can deserialize and persist the latch across app restarts.
- Confirmed short-term memory is already written by the background review, timestamped/expired by `StarMemoryApi`, and injected into the foreground prompt before long-term memory. Added a stronger foreground instruction to treat relevant active short-term items as live current-scene continuity and prioritize them over older memory when they conflict.

Build note: this environment does not have the .NET SDK installed, so `dotnet build` could not be executed here. Source/JSON wiring was checked statically and the Star profile files were parsed with a trailing-comma-tolerant JSON parser.

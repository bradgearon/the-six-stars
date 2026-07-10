# Star Profile Cute Mode and Flirt JSON Update

- Changed `StarProfile.FlirtStyle` from one string to `List<string>`.
- Added `StarProfile.CuteModeInstructions` as `List<string>`.
- Added complete `cuteModeInstructions` and `flirtStyle` arrays to all six Star JSON profiles.
- Updated `AppendGirlfriendModeInstructionSet` to append the selected Star's JSON-driven flirt instructions.
- Cute-mode instructions are appended only when girlfriend cute mode is enabled and the current hourly girlfriend state has `CuteModeActive=true`.
- Removed the hardcoded per-Star cute-signature sentence from the girlfriend prompt builder.
- Updated private romance prompt construction and fallback profiles for the new list-based `FlirtStyle` property.

Validation note: all six Star JSON files parse successfully. A .NET build could not be run in the packaging environment because the `dotnet` command is not installed.

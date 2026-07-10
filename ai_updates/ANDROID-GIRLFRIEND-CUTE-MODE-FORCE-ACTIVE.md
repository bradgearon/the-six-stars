# Android Girlfriend Cute Mode Force Active Button

Adds a manual test button to the Android girlfriend settings popup.

## Why
The previous setting only enabled or disabled the deterministic cute-mode roll. For testing girlfriend cute-mode dialogue, the player needs a button that sets the current girlfriend hourly state to cuteModeActive=true immediately.

## Behavior
- Tap the selected Star sprite while she is girlfriend.
- Girlfriend Settings opens.
- `Allow cute mode` still controls whether cute mode may happen normally.
- New `Cute now` button immediately:
  - sets `GirlfriendCuteModeEnabled=true`
  - sets `GirlfriendHourlyState.IsAvailable=true`
  - sets `GirlfriendHourlyState.CuteModeActive=true`
  - sets `GirlfriendHourlyState.CuteModeProbability=1.0`
  - sets the Star-specific cute-mode signature
  - records a relationship event for debugging
- The next foreground chat/story/listen prompt sees `cuteMode=true` and should use the Star-specific cute girlfriend style where appropriate.

## API
New mobile dispatch command:

```json
{
  "commandType": "SetGirlfriendCuteModeActive",
  "starId": "Kaelia",
  "cuteModeActive": true,
  "reason": "Android girlfriend settings forced cute mode active for testing."
}
```

Aliases accepted by the dispatcher include `ActivateGirlfriendCuteMode`, `ActivateCuteMode`, `ForceCuteMode`, and `CuteModeNow`.

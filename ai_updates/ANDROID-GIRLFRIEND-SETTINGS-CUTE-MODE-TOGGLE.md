# Android Girlfriend Settings: Cute Mode Toggle

Adds a girlfriend-only settings popup to the Android production UI.

## Behavior

- Tapping the selected Star sprite/stage opens girlfriend settings when the selected Star is currently girlfriend.
- If the selected Star is not girlfriend, tapping the sprite/stage opens the Star selector as before.
- The existing title/name area still opens the Star selector.
- The visible prod-mode Break up button is no longer shown on the main screen; Break up now lives inside the girlfriend settings popup.

## Settings popup

The girlfriend settings popup includes:

- Current girlfriend status.
- Cute-mode setting: enabled/disabled.
- Current cute-mode runtime state: active/not active.
- Cute-mode signature for the selected Star.
- `Allow cute mode` checkbox.
- `Save` button.
- `Break up` button with confirmation.

## API / runtime

- Added mobile command: `SetGirlfriendCuteMode`.
- Added command payload field: `cuteModeEnabled`.
- Added durable state field: `StarState.GirlfriendCuteModeEnabled` defaulting to `true`.
- Added runtime method: `SetGirlfriendCuteModeEnabledAsync(...)`.
- If cute mode is disabled, the girlfriend hourly deterministic cute-mode roll is suppressed and current `CuteModeActive` is forced false.
- Prompt builders now pass `girlfriendCuteModeEnabled` alongside the active cute-mode state.
- If disabled, girlfriend mode remains affectionate but avoids the special cute-mode signature behavior pattern.

## Mobile state/events

Android `/api/mobile/state` and bridge events now include:

- `girlfriendCuteModeEnabled`
- `girlfriendCuteModeActive`
- `girlfriendCuteModeSignature`

`SetGirlfriendCuteMode` publishes `GirlfriendCuteModeChanged`, which Android treats as a state event.

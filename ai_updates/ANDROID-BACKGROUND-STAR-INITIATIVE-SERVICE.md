# Android background Star initiative service

This update adds an Android `Service` that can quietly check whether the selected Star has something to say while the phone app is open or has just resumed/reconnected.

## Android behavior

- Added `StarInitiativeBackgroundService`.
- The service starts when the Android activity opens/resumes if both toggles are enabled:
  - `Let selected Star speak at rest`
  - `Background check service after a few quiet seconds`
- The service performs an initial check after a few seconds, then checks again at randomized quiet intervals.
- The service throttles at-rest checks to at most once every two minutes.
- It sends the existing `MaybeStarInitiative` bridge command, so the desktop runtime still uses the normal Star spoken request path, relationship state, daily JSON, and interstar feedback.
- When the desktop returns a Star initiative event, the service broadcasts it to `MainActivity`.
- `MainActivity` applies the event through the normal bridge-event handler so the Android UI conversation history, response text, debug panel, animation pulse, and phone voice streaming behavior are updated consistently.

## UI toggles

Prod cat profile menu:

- `Let the selected Star speak at rest`
- `Background check service after a few quiet seconds`

Dev screen:

- `Let selected Star speak at rest`
- `Background check service for at-rest lines`

## Reconnect behavior

When the phone voice socket reconnects, Android now nudges the background service to check after a short quiet delay instead of making the foreground activity perform the at-rest request directly.

## Persistence

The service reads shared Android preferences for:

- bridge URL
- selected Star
- prod/dev session mode
- romance mode
- phone voice playback
- area / quest context
- at-rest initiative toggles

This lets the background checker run without relying on the activity's current in-memory UI fields.

## Notes

This is a normal Android started service, not a foreground notification service. It is meant for the active phone companion experience and quiet checks around app open/resume/reconnect. Android may still stop ordinary background services if the app is fully backgrounded for a long time.

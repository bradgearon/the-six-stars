# Daily Star State XML Refresh Update

Adds a small generated daily state file for each Star so normal requests can include a lore-based "today" context.

## Behavior

- The API starts a background daily-state service when the desktop runtime starts.
- The service checks configured local-time refresh slots while the app is running.
- Defaults:
  - morning: 00:00 until before 10:00
  - midday: 10:00 until before 15:00
  - night: 15:00 until before 19:00
- If a request arrives and the selected Star does not have the current slot yet, the runtime refreshes that Star before building the prompt.
- The generated XML is injected into the normal speech/story/photo request under `TODAY STAR STATE XML`.

## Files

Daily XML is written under:

```text
state/daily-star-state/<session>/<star>/<yyyy-MM-dd>/<slot>.xml
state/daily-star-state/<session>/<star>/current.xml
state/daily-star-state/<session>/<star>/current.meta.json
```

## Config

Configured from `appsettings.json` / `appsettings.Local.json`:

```json
"DailyStarState": {
  "Enabled": true,
  "GenerateForAllStars": true,
  "RefreshSelectedStarOnRequest": true,
  "StateRoot": "state/daily-star-state",
  "PollSeconds": 180,
  "MaxXmlCharsForPrompt": 12000,
  "StarIds": [],
  "RefreshSlots": [
    { "SlotId": "morning", "StartsAt": "00:00", "RefreshBefore": "10:00" },
    { "SlotId": "midday", "StartsAt": "10:00", "RefreshBefore": "15:00" },
    { "SlotId": "night", "StartsAt": "15:00", "RefreshBefore": "19:00" }
  ]
}
```

## Mobile bridge

The dispatcher publishes `DailyStarStateUpdated` events. Android displays the current daily state summary under the Star state/emotion line and receives the XML/summary in normal mobile responses.

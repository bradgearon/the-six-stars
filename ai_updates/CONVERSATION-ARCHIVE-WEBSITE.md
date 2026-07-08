# Conversation Archive Website

Adds a local, beautiful review website for **The Last Cat: The Early Years** conversations and Six Stars relationship state.

## How to open

The WPF desktop UI now shows an **Open Archive** button in the Android Phone Bridge panel. The same host/port used by the mobile bridge serves the website:

```text
http://<host>:<port>/archive/
```

By default this uses the configured mobile bridge port, normally `8066`.

## What it reads

The archive page is static HTML/CSS/JS served from `Content/WebArchive`, but it loads live runtime data from:

```text
GET /api/archive/data?sessionId=android-phone-prod&maxLogs=240
```

The payload includes:

- current `SixStarsSaveData` Star states for the selected session
- daily Star state records
- rolling markdown emotion trace files from `logs/conversation-emotion`
- trace markdown containing JSON blocks for player turns, deterministic updates, interstar relays/reactions, combos, and deltas

## UI features

- Branded title: **The Last Cat: The Early Years**
- cat avatar icon from the Android UI assets
- clickable Star portrait strip at the top/left
- per-Star page with stats at the top, portrait and daily state in the middle, conversation history below
- cute instant-message style bubbles for the Last Cat and each Star
- status badges for met, ally, girlfriend-ready, and actual girlfriend
- relationship meters for Heart, Trust, Romance, Connection, Benaveil Influence, and Interactions
- top accumulated emotions from state
- relationship score accumulators and recent relationship events
- search across every Star's conversation traces
- each parsed turn shows top affected emotions, actual relationship deltas, accepted relationship combos, and accepted emotion signals when available

## Configuration

`appsettings.json` now includes:

```json
"ConversationArchive": {
  "Enabled": true,
  "PathPrefix": "/archive",
  "DefaultSessionId": "android-phone-prod",
  "MaxLogs": 160
}
```

The archive is served by the existing lightweight `MobileBridgeHttpServer` so no extra dependency or web framework is needed.

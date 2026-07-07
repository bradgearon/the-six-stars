# Daily JSON + Conversation Emotion Trace Log

This update replaces the daily Star state prompt artifact with deterministic JSON and adds a runtime-level conversation/emotion trace log.

## Daily Star state now writes JSON

New daily state files are written as `current.json` and `<slot>.json` under the existing daily state folder. The old XML fields are retained only as a compatibility fallback so clearing data and regenerating state is safe.

Prompt injection now uses `TODAY STAR STATE JSON` and `todayStarStateJson` instead of XML. The JSON includes identity, relationship, current inner life, today activity, recent awareness, and prompt-use summary.

## Rolling conversation emotion trace logs

A new runtime logger writes markdown files to:

```text
<UI bin folder>/logs/conversation-emotion/
```

A new file is created whenever a primary player-to-Star turn finishes its background emotion/memory review. Interstar ripple entries then append to that same file until the next primary turn settles.

Each log includes:

- player statement and destination Star
- Star response
- source state before and after the update
- raw positive and negative emotional vectors
- top affected emotions
- accepted positive and negative signals
- accepted combo rules
- suggested and actual state deltas
- deterministic scoring summary
- important quotes and relationship events
- interstar relay source/destination Star
- interstar reaction vectors and state deltas
- origin feedback vectors back to the original Star

This makes it easy to send a single trace to ChatGPT for deterministic scoring tuning.

## Desktop UI helpers

The Debug panel now has:

- Load Latest Conversation Log
- Copy Log
- Open In Notepad

The log is selectable/copyable through the debug text box.

## Android Prod conversation order

The Android Prod conversation window now treats every incoming line as newest-first. A Star response appears above the player line once it arrives, because it is the latest event. Restored history from state uses the same newest-first display order.

Android Prod text views are selectable so logs/status text can be copied more easily.

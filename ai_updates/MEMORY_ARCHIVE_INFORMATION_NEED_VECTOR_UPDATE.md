# Memory Archive + Information Need Vector Update

## What changed

- Added a bounded prompt-facing quote list and a durable source quote archive.
- `StarState.ImportantPlayerQuotes` is now the small active list for ongoing prompt context.
- `StarState.AllImportantPlayerQuotes` keeps the source master list with timestamps, emotion/vector metadata, tags, deltas, and marker ids.
- `StarState.MemoryLog` is treated as the recent active conversation only.
- Conversation turns older than the configured archive window are moved into `StarState.OldConversationArchives` with a compact summary and source entries retained for old-memory retrieval.
- Remembered markers stay durable in `StarState.RememberedMarkers`; prompts still receive only selected bounded slices.
- Added foreground `needsMoreInformation` and `informationNeeds` vector fields.
- When a Star needs old history, quotes, markers, lore, bestiary, character, world, screen, or story data, the runtime builds a capped archive slice and performs a second internal LLM pass with that extra context.
- Added Clear All Star Data to desktop and Android.

## How the information need vector works

Normal fast chat now returns:

```json
{
  "starId": "Elusiaa",
  "spokenText": ["One breath, little one. I am reaching back through the older cave-light."],
  "needsMoreInformation": true,
  "informationNeeds": [
    {
      "dataType": "history",
      "subject": "slime gel",
      "confidence": 0.82,
      "score": 82,
      "reason": "The player is asking about an older conversation detail."
    }
  ]
}
```

The runtime recognizes that request, gathers matching local state from quote records, remembered markers, old conversation archives, or Star lore, then calls the same Star again with `informationFollowup=true` and an `EXTRA REQUESTED INFORMATION` block. The final response is the one returned to the desktop or Android client.

## Defaults

- Active conversation log cap: 80 turns.
- Important prompt quote cap: 128 quotes.
- Source quote archive cap: 4096 quote records.
- Conversation archive age: 120 minutes.
- Old conversation archive cap: 80 archive blocks.
- Remembered marker cap: 2048 durable markers.
- Extra requested information context cap: 180,000 characters.


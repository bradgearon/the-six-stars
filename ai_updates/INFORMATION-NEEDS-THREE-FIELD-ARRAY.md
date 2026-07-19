# Information needs: three-field model array

The fast spoken response now asks the model for this exact shape:

```json
"needsMoreInformation": true,
"informationNeeds": [
  { "subject": "bestiary", "confidence": 0.75, "score": 80 }
]
```

Each item has only `subject`, `confidence`, and `score`. `subject` is a source category and must be one of `history`, `quotes`, `markers`, `lore`, `bestiary`, `characters`, `world`, `screen`, or `story`. The original player speech is automatically retained as the lookup question.

The runtime no longer scans player text for slime, HP, creature, spell, character, world, or story keywords to manufacture information needs. It routes only the categories produced by the foreground model.

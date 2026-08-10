# Writing value baselines

`writing-value-baselines.json` is an optional curated calibration pack for the writing shelf.

The value system is open-ended and measured in **information units (IU)**: values must be non-negative, but there is no maximum. They are literary calibration units, not dollars, percentages, probabilities, or prices. `referenceValue` defaults to `100` IU; it is a reference point for excellent known work, not a ceiling. A future work may legitimately receive values above the best examples when it is materially stronger.

Each optional example can use either a compact 27-value `valueVector` or a complete named `values` object. Example shape:

```json
{
  "version": 1,
  "unit": "information-units",
  "referenceValue": 100,
  "examples": [
    {
      "id": "kaelia-baseline-1",
      "starId": "Kaelia",
      "outputType": "constellation-story",
      "title": "Example title",
      "writing": "Full untruncated work...",
      "values": {
        "Funny": 20,
        "Deep": 112,
        "Intrigued": 104,
        "Interested": 101,
        "Poetic": 118,
        "Clever": 110,
        "Beautiful": 115,
        "Moving": 105,
        "Memorable": 114,
        "Original": 109,
        "Witty": 65,
        "Playful": 25,
        "Surprising": 100,
        "Mysterious": 116,
        "Haunting": 111,
        "Melancholy": 102,
        "Hopeful": 72,
        "Romantic": 40,
        "Vivid": 113,
        "Coherent": 110,
        "Meaningful": 116,
        "Characterful": 119,
        "EmotionallyResonant": 108,
        "Polished": 111,
        "Quotable": 117,
        "WorthRetelling": 115,
        "Overall": 114
      }
    }
  ]
}
```

When curated examples are absent or do not fill the requested reference set, the app automatically fills the remaining slots from the highest-valued compatible works already persisted on that Star's shelf.

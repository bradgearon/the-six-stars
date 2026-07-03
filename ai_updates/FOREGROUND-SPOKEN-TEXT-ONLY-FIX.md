# Foreground Spoken-Text-Only Fix

The fast foreground LLM pass now requests only the text that must be spoken immediately.

## Why

The foreground request was still using a compact-but-emotional schema that asked for fields such as `emotionalPurpose`, `voiceDirective`, and `topEmotions`. In practice, the local model sometimes continued producing the full emotion/memory object, which delayed speech.

## New foreground contract

```json
{
  "starId": "Elusiaa",
  "spokenText": [
    "One or two natural sentences in this chunk.",
    "The next one or two natural sentences, spoken after the first."
  ]
}
```

The foreground schema does not contain:

- `voiceDirective`
- `emotionalPurpose`
- `topEmotions`
- `emotionScores`
- `suggestedStateDeltas`
- memory or summary fields
- lore/risk/debug fields

## Voice behavior

The runtime builds the initial TTS `VoiceDirective` deterministically from the selected Star profile. This lets the Star speak quickly without asking the LLM to score emotions first.

## Background review

The background request still uses the full schema and is explicitly labeled:

```text
six_stars_background_emotion_memory_review
```

That background request computes emotion scores, summaries, durable memories, relationship events, learned functions, suggested deltas, and the deeper voice-performance review.

## Debugging

The foreground request should show this schema name:

```text
six_stars_fast_spoken_text_only
```

If a log shows `emotionScores` or the schema name `six_stars_background_emotion_memory_review`, that is the background analysis request, not the foreground speech request.

# Fast spoken parts + background analysis update

## Summary

This update splits the Star turn into two LLM passes:

1. **Fast foreground speech pass** — returns only the spoken reply, voice directive, emotional purpose, and top emotion hints.
2. **Background emotion/memory review pass** — runs after the foreground response is returned so TTS can begin immediately, then applies deterministic relationship deltas and memory updates when the review settles.

## Spoken response contract

`spokenText` is now an array of ordered speech chunks:

```json
"spokenText": [
  "Oh, little one. I heard you clearly.",
  "Let me answer softly, and then I will think through what this means for us."
]
```

Each item should be one or two natural sentences. The TTS server now uses these AI-provided chunks directly and does not split by punctuation, max words, or sentence heuristics when `textParts` is provided.

## Runtime behavior

- `StructuredStarAgent.GenerateAsync(...)` performs the fast speech pass.
- `StructuredStarAgent.AnalyzeTurnAsync(...)` performs the full emotion/memory review pass.
- `SixStarsRuntime.ProcessAsync(...)` returns the fast response immediately and queues background analysis.
- `SixStarsRuntime.BackgroundAnalysisCompleted` fires when the deterministic relationship update and memory write are complete.
- `SixStarsRuntime.CancelBackgroundAnalysis(...)` is available for UI/Unity pause, scene changes, or cancel-all operations.

## TTS behavior

- `VoiceSynthesisRequest.TextParts` carries the ordered spoken chunks.
- `WebSocketTtsVoiceGenerator` sends `textParts` to the Python WebSocket server.
- `websocket_tts_server.py` uses `textParts` as the chunk list directly.
- If `textParts` is missing, the old deterministic text splitter remains as a fallback only.

## Config

`LLM.SpeechMaxTokens` defaults to `900` and can be overridden with:

```bat
set SIX_STARS_LLM_SPEECH_MAX_TOKENS=900
```

Full background review still uses `LLM.MaxTokens`.

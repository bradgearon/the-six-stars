# Compact Background Emotion Review + Voice-Ready Release Update

This update changes the live companion turn pipeline so voice generation stays first-priority and the heavy emotion/memory work happens only after foreground voice generation reports ready.

## Foreground pass

The first LLM call remains a fast spoken response pass. It returns only the player-facing speech fields:

- `starId`
- `spokenText` as an ordered array of TTS-ready chunks
- story fields only when story mode needs them

It does not request emotion scores, voice directives, memory notes, relationship deltas, lore fields, or learned functions.

## Voice-priority background release

`SixStarsRuntime.ProcessAsync(...)` now stores a pending background analysis job and returns immediately with `BackgroundAnalysisId` on the `StarInteractionResponse`.

Hosts should call:

```csharp
runtime.StartBackgroundAnalysisAfterVoiceReady(response.BackgroundAnalysisId, "Foreground voice generation completed");
```

The WPF app now does this automatically when the streaming voice pipeline raises `AllChunksComplete`. This means Qwen/WebSocket speech generation gets priority before the background LLM review starts.

## Compact background review

The second LLM call now uses `six_stars_background_emotion_memory_review_compact`.

It receives the exact `spokenText` array from the foreground response so the Star knows what she said. It must copy that same array into its response for memory logging.

The background response no longer asks for:

- `voiceDirective`
- `emotionalPurpose`
- full `storyMarkdown`
- `canonSourcesUsed`
- all 32 emotions

Instead it asks for no more than 9 impactful `emotionScores`. Neutral emotions should be omitted. The deterministic relationship system still computes the final applied deltas.

## Config

Added:

```json
"ReviewMaxTokens": 1600
```

Environment override:

```bat
set SIX_STARS_LLM_REVIEW_MAX_TOKENS=1600
```

## Unity/API integration

The public facade also exposes:

```csharp
StartBackgroundAnalysisAfterVoiceReady(backgroundAnalysisId, reason)
CancelBackgroundAnalysis(reason)
HasPendingOrRunningBackgroundAnalysis
```

This makes it easy for Unity to defer incoming speech/input later if the game needs fully settled state before accepting the next command.

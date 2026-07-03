# Conversation Memory Prompt Update

This update makes every Star LLM request carry the relationship and conversation continuity needed for the Six Stars companion loop.

## What changed

- Default prompt history is now the **last 10 conversation turns**.
- The history count is configurable through appsettings or environment variables.
- The prompt now begins with a concise **LIVE SCENE BRIEF**:
  - who the Star is,
  - that she is talking to the Last Cat,
  - what he just said,
  - what he wants now,
  - what they were just talking about,
  - shared durable memory,
  - relationship status,
  - and the latest screen/vision update.
- The prompt includes a fuller memory block:
  - existing conversation summary,
  - important exact player quotes,
  - memory notes,
  - relationship events,
  - girlfriend history,
  - learned functions,
  - unlocked/detailed secrets,
  - relationship combos,
  - last N player/star turns.
- The prompt explicitly requires every model reply to return:
  - `conversationSummary`,
  - `changesSummary`,
  - `importantPlayerQuotes` when impactful,
  - `memoryNotes` when durable.
- If the model leaves `conversationSummary` or `changesSummary` blank, `StructuredStarAgent` now creates a fallback summary so the Star state still gets updated.

## Config

`src/Wds.SixStars.Ui/appsettings.json`

```json
"Memory": {
  "RecentHistoryForPrompt": 10,
  "MaxMemoryLogEntries": 160,
  "MaxImportantQuotes": 80,
  "MaxMemoryNotes": 160,
  "ImportantQuotesForPrompt": 24,
  "MemoryNotesForPrompt": 40,
  "RelationshipEventsForPrompt": 30,
  "GirlfriendHistoryForPrompt": 20,
  "LearnedFunctionsForPrompt": 25,
  "SecretStatesForPrompt": 30,
  "RelationshipCombosForPrompt": 20,
  "ScreenCapturesForPrompt": 5
}
```

Environment overrides:

```bat
set SIX_STARS_RECENT_HISTORY_FOR_PROMPT=10
set SIX_STARS_IMPORTANT_QUOTES_FOR_PROMPT=24
set SIX_STARS_MEMORY_NOTES_FOR_PROMPT=40
set SIX_STARS_RELATIONSHIP_EVENTS_FOR_PROMPT=30
set SIX_STARS_GIRLFRIEND_HISTORY_FOR_PROMPT=20
set SIX_STARS_LEARNED_FUNCTIONS_FOR_PROMPT=25
set SIX_STARS_SECRET_STATES_FOR_PROMPT=30
set SIX_STARS_RELATIONSHIP_COMBOS_FOR_PROMPT=20
set SIX_STARS_SCREEN_CAPTURES_FOR_PROMPT=5
```

## Where it is implemented

- `StarPromptOptions.cs` controls prompt memory limits.
- `StarPromptBuilder.cs` builds the live scene brief and memory/context sections.
- `StructuredStarAgent.cs` fills fallback summaries when the model omits them.
- `SixStarsRuntime.cs` defaults recent history to 10 and passes memory settings to the state API.
- `MainViewModel.cs` reads memory prompt settings from config/environment.

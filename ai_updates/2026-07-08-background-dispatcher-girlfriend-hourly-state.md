# Background AI Dispatcher + Girlfriend Hourly State Update

## Summary
This update moves runtime-managed background AI work behind an API-owned dispatcher and adds a girlfriend-only hourly state pass for the selected Star.

## Background AI Dispatcher
- Added `BackgroundAiRequestDispatcher` under `Wds.SixStars.Api.Background`.
- The dispatcher owns a priority queue for background AI stacks and emits status events for queued/started/completed/failed work.
- Foreground reply requests enter a dispatcher foreground lease so queued background AI does not start while the API is producing a player-facing reply.
- Existing runtime background stacks now enqueue through the dispatcher:
  - scheduled daily Star state refreshes
  - primary emotion/memory background analysis
  - async story generation
  - girlfriend proposal decision generation
  - interstar update cycle after settlement delay
  - girlfriend hourly state refresh

## Girlfriend Hourly State
- Added `StarGirlfriendHourlyStateContext` to `StarState` so girlfriend state is saved with the Star.
- Added girlfriend hourly vector models with `needVector` and `wantVector` items that include `confidence` and `score`.
- Added an LLM response format and prompt path for `IStarGirlfriendHourlyStateGenerator`.
- The prompt includes a girlfriend need catalog and want catalog with items such as physical affection, reassurance, kisses, emotional safety, quality time, cute lines, playful teasing, asking for cuddles, and wanting to compliment the Last Cat.
- The generated state stores summaries, need/want vectors, and candidate things she wants to say or cute things she wants to say later.
- The runtime schedules girlfriend state refreshes hourly for the current girlfriend and also queues a forced refresh when a Star becomes the exclusive girlfriend.

## Star Initiative / Autospeak
- Star initiative now receives the girlfriend hourly state in the prompt.
- The initiative gate now considers girlfriend needs, wants, and candidate speech hooks as state-backed reasons to ask the LLM whether she wants to speak.
- The Star initiative prompt explicitly treats the check as two outputs: first decide `hasSomethingToSay` / `shouldSpeak`, then provide `spokenText` only when she chooses to say something.

## Android
- Android now exposes one option for at-rest Star speech: `Let selected Star speak at rest`.
- The old separate background autospeak flag is mirrored to the same value for backward compatibility.
- The at-rest Star speech option defaults to `true` on Android.

## Notes
- Full .NET/Gradle builds were not run in this container because the required SDKs are not installed here.
- Static syntax checks were run on the modified C# files for brace/string-state balance.

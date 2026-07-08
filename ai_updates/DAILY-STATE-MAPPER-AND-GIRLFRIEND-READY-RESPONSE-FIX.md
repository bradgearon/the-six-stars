# Daily State Mapper and Girlfriend-Ready Response Fix

## What changed

- Fixed the daily Star state mapper so rich LLM output is not lost when the model returns the saved/runtime nested shape instead of the requested top-level schema.
- Added flexible alias extraction for daily-state fields:
  - `currentInnerLife.feelingsSummary` or `currentInnerLife.feelings` -> `FeelingsSummary`
  - `currentInnerLife.desires` or `currentInnerLife.currentDesires` -> `CurrentDesires`
  - `today.whatSheIsDoing` -> `WhatSheIsDoingToday`
  - `today.wantsToTalkAbout` -> `ThingsSheWantsToTalkAbout`
  - `recentAwareness.peopleSeen/events` -> recent awareness arrays
  - `promptUse.summary` -> `PromptUseSummary`
- The saved/injected daily JSON now includes both canonical runtime names and LLM-friendly aliases for inner-life fields:
  - `feelings` and `feelingsSummary`
  - `desires` and `currentDesires`
- Strengthened daily-state prompt instructions to ask for top-level fields, while keeping the runtime mapper tolerant of nested output.
- Strengthened girlfriend-ready foreground instructions so emotional statements and compliments receive a more direct, personal, personality-matched response before returning to lore/philosophy.

## Why

The daily-state LLM was producing good content, but a nested shape like:

```json
"currentInnerLife": {
  "feelingsSummary": "...",
  "desires": "..."
}
```

was being deserialized into a top-level `StarDailyStateModelResponse`, leaving `SituationSummary`, `FeelingsSummary`, `CurrentDesires`, and related fields empty. The foreground prompt then received blank `TODAY STAR STATE JSON` despite the LLM producing good content.

## Expected result

The next daily refresh should preserve the rich Kaelia state in the foreground prompt, including her situation, feelings, relationship posture, desires, hooks, current activity, and wants-to-talk-about topics.

When Kaelia is girlfriend-ready, a line such as “my wisdom is precious just as you are Kaelia” should not get only a distant philosophical response. She should first register the personal compliment in her guarded/intimate Kaelia style, then continue with her Void/Astral Stream answer.

# Conversation History Prompt Visibility Fix

This update makes recent conversation history explicit and harder to miss in both the fast foreground speech request and the background emotion/memory review request.

## Why

Recent logs already showed some continuity data under `Ongoing summary`, `Last remembered reply`, and `Most recent usable turns`, but the section was easy to overlook and only the foreground prompt showed a short 4-turn compact list. The Android prod companion flow also made it look like remembered markers were present while conversation history was missing.

## Changes

- Added a clearly labeled prompt block:
  - `RECENT CONVERSATION HISTORY FROM MEMORY LOG — newest first`
- Increased foreground recent history from 4 turns to up to 10 turns, capped by `StarPromptOptions.RecentHistoryCount`.
- Increased background review recent history to the same up-to-10-turn window.
- Background review history now includes timestamps, request type, player text, Star reply, summary, impactful quotes, relationship events, and tags when available.
- Foreground prompts now include a compact `IMPORTANT MEMORY AVAILABLE TO THIS FOREGROUND REPLY` block with:
  - important exact player quotes
  - memory notes
  - relationship events
  - stories already told by the selected Star
- Remembered markers remain included separately.
- Technical parser-fallback turns remain filtered out so the Star does not roleplay from broken fallback text.

## What should now appear in request logs

In the fast-spoken request user message, you should see:

```text
RECENT CONVERSATION HISTORY FROM MEMORY LOG — newest first. Use this for continuity, follow-ups, pronouns, tone, and relationship pacing.
- Turn 1: requestType=...; player="..."; Elusiaa="..."
  summary=...
```

In the background review request, the same section will appear with fuller details:

```text
RECENT CONVERSATION HISTORY FROM MEMORY LOG — newest first. Use this for continuity, follow-ups, pronouns, tone, and relationship pacing.
- Turn 1 (most recent) at ...; requestType=...
  player="..."
  Elusiaa="..."
  summary=...
```

## Notes

If a turn still does not appear, it is usually because it was filtered as unusable: empty player text, empty Star response, or parser fallback response. That is intentional because fallback lines pollute character memory.

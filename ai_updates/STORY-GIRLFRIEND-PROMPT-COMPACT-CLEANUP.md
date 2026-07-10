# Story / Girlfriend Prompt Compact Cleanup

This update cleans the Story foreground request path after traces showed Story clicks carrying too much girlfriend/hourly/interstar/debug state into the LLM prompt.

## Goals

- A Story button click with no text is a complete request: the Star should invent and tell a new complete story.
- Do not ask the model to invent a story seed first.
- Do not send per-request `max_tokens` caps because some local backends silently fail or truncate when a cap is present.
- Do not inject raw girlfriend hourly JSON into foreground prompts.
- Store and present girlfriend needs/wants as compact vector strings, not verbose human-readable objects with reasons.
- Prevent background interstar ripple work from competing with foreground Story generation.

## Story request changes

- Story mode now explicitly says the Story button itself is sufficient instruction.
- Empty player text means: invent a fresh complete episode now.
- Story mode tells the model to create a concrete place, character/problem, escalation, surprise/turn, and ending.
- Story mode no longer treats the empty player text as a mood prompt.
- Story mode says girlfriend state is tone/color only, not a replacement for story plot.

## Girlfriend prompt cleanup

The foreground prompt now sends compact current signals instead of raw hourly JSON:

```text
summary=...
relationshipPosture=...
needTheme=...
wantTheme=...
cuteMode=true; probability=0.35; signature=...
needVector=itemId:score:confidence;itemId:score:confidence
wantVector=itemId:score:confidence;itemId:score:confidence
```

Raw hourly JSON remains available for debug/state storage but is not injected as raw prompt context.

## Durable girlfriend history cleanup

Durable needs/wants history now has compact vector string lists:

```text
GirlfriendDesiredNeedVectorHistory: timestamp|slot|itemId:score:confidence;...
GirlfriendDesiredWantVectorHistory: timestamp|slot|itemId:score:confidence;...
GirlfriendMetNeedVectorHistory: timestamp|slot|itemId:score:confidence;...
GirlfriendMetWantVectorHistory: timestamp|slot|itemId:score:confidence;...
```

The old verbose object histories remain for backward compatibility, but foreground prompt injection prefers the compact lists.

## Removed bloat

- Vector reasons are cleared from stored girlfriend need/want vectors.
- Raw girlfriend hourly JSON is not injected into prompts.
- Durable girlfriend history sent to prompts is limited to compact aggregate vectors and a few recent compact vector lines.
- Couple activity state is sent as compact scores plus at most two high-scoring player examples.
- Interstar relay reasons are kept out of the LLM prompt.

## Runtime priority

Story requests are no longer eligible for interstar ripple cycles, so a Story click will not enqueue Star-to-Star background vector calls before/around foreground story generation.

## Token cap policy

The runtime now returns `0` for speech, story, and review max-token resolver methods so the request body does not send per-pass `max_tokens`. Output size is controlled by prompt compactness and response schema/format instructions instead.

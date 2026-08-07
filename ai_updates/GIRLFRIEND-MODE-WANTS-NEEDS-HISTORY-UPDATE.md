# Girlfriend Mode Wants/Needs History Update

This update expands girlfriend mode from a one-time hourly desire snapshot into a durable relationship-performance loop.

## Runtime / API behavior

- Background AI work remains API-owned through the dispatcher so foreground replies keep priority over state refreshes.
- Daily Star state now uses three 8-hour local-time slots by default: `slot_00_08`, `slot_08_16`, and `slot_16_24`.
- Every AI prompt built by the API now receives a `CURRENT TIME` block with local date, local time, UTC offset, and local time-zone display name.
- JSON state still exports as the whole session save, but the JSON store also writes a separate durable shard per Star under `state/stars/<sessionId>/<starId>.json`.

## Girlfriend mode instructions

When a Star is marked as the Last Cat's girlfriend, the prompt now includes a dedicated girlfriend-mode instruction section. The Star is told to be:

- cute, endearing, responsive, emotionally present, and actively flirty when appropriate;
- aligned with her own personality, element, domain, trust level, and connection;
- guided by her current `needVector`, `wantVector`, and accumulated needs/wants-met performance vectors;
- non-explicit and active-flirt by default;
- careful to review recent conversation before using a wanted-to-say hook so she does not repeat lines she already said.

## Durable girlfriend vectors

`StarState` now stores durable girlfriend history chunks:

- `GirlfriendDesiredStateHistory` — generated wants/needs and things she wanted to say.
- `GirlfriendNeedsMetHistory` — what the Last Cat appears to have met in each turn.
- `GirlfriendNeedsMetPerformanceVector` — accumulated met needs since girlfriend path began.
- `GirlfriendWantsMetPerformanceVector` — accumulated met wants since girlfriend path began.

These chunks reset when the girlfriend path ends or another Star becomes the exclusive girlfriend.

## Background review fulfillment pass

The background review schema and prompt now ask for:

- `girlfriendNeedsMetSummary`
- `girlfriendNeedsMetVector`
- `girlfriendWantsMetVector`

When the Star is a girlfriend, the review compares the player's latest message and the Star's spoken reply against her current girlfriend vectors. Matching met wants/needs are saved durably and accumulated into performance vectors.

## Fresh wanted-to-say hooks

The girlfriend hourly state continues to generate `thingsSheWantsToSay` and `cuteThingsSheWantsToSay`. Daily state refreshes now preserve the current hooks into durable history and clear the live hooks so the next girlfriend state refresh can produce fresh ones.

## Star initiative speech scoring

Star initiative output now includes a one-item `speechDecisionVector` for `saySomethingNow` with score/confidence. The runtime randomizes an acceptance threshold and combines score + confidence, with girlfriend mode allowing low-score lines to pass when any useful confidence exists and there is actual text to say.

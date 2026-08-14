# Forge Service Boundary — Phase 1

This pass starts moving the reusable cognition pieces out of the desktop/runtime shape and behind an in-process service boundary named **Forge**. It deliberately does **not** add HTTP/WebSocket transport yet. The goal is to make the boundary real before making it remote.

## What moved behind Forge

### Background interaction review

`SixStarsRuntime` no longer invokes `IStarTurnAnalyzer` directly for the voice-ready background emotion/memory review. It queues a `ForgeInteractionReviewRequest` through `IForgeInteractionReviewService`.

The current implementation is `StarTurnAnalyzerForgeInteractionReviewService`, an adapter around the existing `StructuredStarAgent`. This preserves review behavior while making the runtime depend on a Forge contract that can later be implemented by a remote client.

The request carries:

- a stable `ForgeActorReference` (`forge://six-stars/actors/<ActorId>`),
- a structured `ForgeContextBundle`,
- and, temporarily, the legacy `StarProfile` / `StarState` / interaction objects required by the existing analyzer.

Those legacy fields are explicitly migration compatibility payloads, not the intended remote contract.

### Writing/content generation

The desktop no longer calls `StarWritingService.GenerateAndReviewAsync` directly. Shelf generation now goes through `IForgeContentGenerationService` / `IForgeService`.

The three existing generator IDs are stable Forge generator IDs:

- `semantic-collision`
- `verb-noun-displacement`
- `constellation-forge`

`auto` preserves the existing configured weighted selection.

`StarWritingService` now also accepts an optional explicit method id. This lets a future Forge caller request a specific generator without changing the existing automatic shelf loop.

The current adapter is `SixStarsForgeContentGenerationService`; orchestration still lives in `StarWritingService` in this phase. Moving each technique into its own generator implementation is a later mechanical extraction once callers are fully behind the Forge contract.

## Actor reference

`ForgeActorReference` is now the identity intended to cross Forge boundaries instead of copying a whole character JSON/profile:

```text
forge://six-stars/actors/Elusiaa
```

It contains `ForgeId`, `ActorId`, and `ActorType`.

## Context cards

Prompt context now has a structured intermediate representation under `Forge/Context`:

- character
- relationship
- emotion
- memory
- setting
- conversation

`SixStarsForgeContextCardFactory` projects the current profile/state into these cards. `CompactForgeContextCardRenderer` converts cards to prompt text.

The background review prompt is the first live consumer of the card pipeline. This separates **what context exists** from **how a particular model prompt renders it**. Later renderers can emit compact text, JSON, remote-service packets, or model-specific card formats without making state storage own prompt formatting.

## State facets

`ForgeActorStateProjector` now exposes a compatibility snapshot split into:

- `ForgeEmotionStateFacet`
- `ForgeRelationshipStateFacet`
- `ForgeQuoteStateFacet`
- `ForgeMemoryStateFacet`

The physical save file remains the existing `StarState` store in this first pass. The snapshot is the seam for the next state-storage refactor: each facet can move to its own repository/schema while old code temporarily consumes an assembled legacy `StarState` view.

## Transport packet groundwork

`ForgeServiceRequest`, `ForgeServicePacket`, and `ForgeStateUpdateNotice` are transport-neutral contracts for the future parent Forge / Six Stars Forge relationship. No network endpoint is enabled by this change.

The intended future flow is:

```text
larger Forge
  -> actor.respond / interaction.review / content.generate / voice.render
  -> ForgeActorReference
Six Stars Forge
  -> service packet + state facet update notices + utterance/audio refs
```

## Behavior intentionally unchanged

- Existing Star JSON/profile files remain authoritative.
- Existing emotion-vector review semantics remain intact.
- Existing deterministic relationship calculation and `StarMemoryApi` persistence remain intact.
- Existing Spark/Constellation generation, review, persistence, playback, and X-posting behavior remain intact.
- No HTTP/WebSocket server was added yet.

## Next extraction seam

The next useful phase is to make background review return explicit Forge state deltas and route them through facet repositories (`Emotion`, `Relationship`, `Quotes`, `Memory`) instead of having `SixStarsRuntime` construct one monolithic `StarStateUpdateRequest`. After that, the three generator orchestration bodies can move from `StarWritingService` into separate `IForgeContentGenerator` implementations with little caller impact.

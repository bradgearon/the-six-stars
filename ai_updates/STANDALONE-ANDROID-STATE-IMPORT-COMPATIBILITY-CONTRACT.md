# Standalone Android State / Game Import Compatibility Contract

Date: 2026-08-26

## Purpose

The standalone Android app is a portable Star relationship/state runtime, not a second independent game-state design.

Its persistent Star state must remain compatible with the desktop/game Six Stars state model so a future import can move a player's relationship history into The Last Cat without reinterpreting, guessing, or reconstructing what the standalone app meant.

The rule for future work is:

> Keep the state API, field meanings, deterministic relationship/memory behavior, Star profile semantics, and prompt/review contracts aligned with the canonical desktop/game runtime. If the canonical runtime changes, port or explicitly migrate the Android representation rather than allowing a second schema or second interpretation to evolve.

## Canonical compatibility boundary

The desktop/game implementation remains the canonical definition for:

- `SixStarsSaveData` / `StarState` field names and meanings.
- Heart, Trust, Romance, Benaveil influence, emotional connection, girlfriend readiness, girlfriend state, and related thresholds.
- Deterministic emotion-vector scoring and deterministic relationship deltas.
- Positive/negative emotion totals, succession counters, combo behavior, and accumulated relationship scoring.
- Conversation-memory semantics, logical turn timestamps, short-term memory, durable learned information, questions/resolutions, important quotes, markers, secrets, learned functions, relationship events, girlfriend history, couple-activity state, stories, and 10/100-turn rollups.
- The protected primary background-review contract: the LLM supplies evidence/structured review output; deterministic code owns durable state mutation.
- Star profile field names and semantics, including `emotionInfluence`, combo rules, character attributes, flirt/cute/girlfriend instructions, voice metadata, and other profile-driven behavior.
- Prompt-template inputs and structured review outputs that feed deterministic state updates.

Android classes such as `StarStateStore`, `MemoryStateUpdater`, `DeterministicRelationshipEngine`, `StarProfileRepository`, and `AndroidPromptBuilder` are ports/adapters of those concepts. They should not invent Android-only meanings for canonical fields.

## State-file rule

The standalone save file is currently:

`<app files>/state/six-stars-standalone-state.json`

It intentionally uses PascalCase names shaped like the desktop `SixStarsSaveData` / `StarState` model.

The standalone runtime may add clearly namespaced transport/runtime metadata, such as `StandaloneMetadata` or `StandalonePendingPrimaryReview`, but those fields must never change the meaning of canonical game fields. A future importer may ignore standalone-only operational metadata.

If the persistence engine changes later (for example, JSON -> SQLite/Room), the import/export API must still be able to materialize the same canonical state DTO/JSON shape and semantics.

## Profiles and content

The standalone project bundles a snapshot of the desktop `Wds.SixStars.Api/Content` tree and materializes it locally on the device.

Star profiles should remain sourced from the canonical profile JSON files rather than being re-authored as unrelated Android profile classes. When a canonical profile field is added or its meaning changes, the Android loader/logic must be updated to preserve that meaning.

The same principle applies to prompt templates: Android may use a comparable renderer because Scriban itself is not the Android runtime, but the template inputs, behavioral instructions, structured review fields, and resulting state semantics should remain equivalent to desktop.

## Deterministic update rule

Do not move relationship/state authority into the hosted LLM.

The intended sequence remains:

1. Foreground LLM creates the Star's spoken reply.
2. A protected structured background review extracts evidence/state suggestions.
3. Deterministic code applies the canonical relationship, memory, and state rules.
4. The resulting state is durably persisted before the next foreground turn is allowed to depend on it.

The LLM must not independently redefine Heart/Trust/Romance growth, connection thresholds, girlfriend readiness, memory retention rules, or other deterministic state behavior.

If desktop deterministic logic changes, Android should receive the same logic or an explicitly versioned equivalent before standalone state generated under the new rules is considered import-compatible.

## Import hygiene

A future standalone -> game import should be a controlled merge, not a blind file replacement.

Relationship/memory/personality-state fields can be imported according to an explicit versioned mapping. Game-owned progression/discovery state remains authoritative unless the importer explicitly supports it.

In particular, the standalone app currently makes all six Stars socially accessible. That must **not** automatically mean the game should mark all six Stars as discovered through gameplay. The existing `StandaloneMetadata.ImportPolicyHint` records this distinction.

Before import, validate at minimum:

- save/schema version;
- known `StarId` values;
- canonical field types;
- logical timestamp parsing;
- relationship-score bounds;
- emotional connection bounds;
- profile/version compatibility when deterministic calculations depend on changed profile data;
- duplicate memory/event IDs where applicable;
- standalone-only metadata versus game progression fields.

Import should be idempotent where practical so importing the same standalone state twice does not duplicate memories, markers, stories, relationship events, or rollups.

## Runtime guardrails for the standalone hosted app

The standalone app is intentionally request/event driven.

- It does **not** use a periodic polling loop for conversation, memory review, ASR, TTS, daily state, or girlfriend-hourly state.
- Daily and girlfriend-hourly maintenance is checked after a committed interaction and only generated when the saved state is stale.
- Google streaming uses the provider's streaming HTTP/SSE response; reading that stream is not polling.
- The short `Thread.sleep` calls in protected-review retry handling are retry backoff after an actual failed request, not periodic polling.
- The standalone Google client deliberately does **not** send `maxOutputTokens` for foreground or structured/background requests. The model is allowed to finish on EOS/context/provider limits, matching the canonical desktop no-request-token-cap behavior.
- Network request timeouts remain safety timeouts; they are not output-token limits.

Do not add polling or artificial output-token caps merely as a mobile optimization without an explicit design decision, because either can change the timing/behavioral contract relative to the game runtime.

## When a canonical desktop change is made

For every change touching state, memory, deterministic relationship calculations, profiles, or review/template contracts, check both runtimes:

1. What changed in the canonical desktop/game API or semantics?
2. Does standalone Android represent the same field and meaning?
3. Does `MemoryStateUpdater` still perform the equivalent durable mutation?
4. Does `DeterministicRelationshipEngine` still calculate the equivalent result?
5. Does the local profile snapshot expose the same inputs?
6. Does the Android template/review adapter produce the same evidence fields needed by deterministic code?
7. Is an explicit state migration/version bump needed?
8. Can the resulting standalone state still be exported to the canonical import DTO without semantic loss?

This compatibility review is part of the definition of done for future standalone state-system changes.

## Interstar relationship isolation

Interstar relay/reaction state is not a substitute for direct relationship experience with the Last Cat.

The canonical rule is:

- second-hand interstar impressions may influence private awareness and separate Star-to-Star relationships;
- they must not award or remove Last Cat Heart, Trust, Romance, or EmotionalConnectionPercent;
- they must not enter direct-player relationship accumulators/vector history where they could later cash out as relationship points;
- only direct Last Cat interactions may mutate those player relationship meters.

The desktop runtime enforces this for `interstarMicroUpdate=true`. Standalone Android does not currently run the desktop interstar scheduler; when that scheduler is ported, this isolation rule is mandatory for import-compatible state.

## Important quote authority

Important quote significance is again background-LLM-selected, not inferred from ordinary emotion magnitude. The deterministic layer remains responsible only for validation/durability: an LLM-selected exact quote must occur verbatim in the current player turn before it is persisted. This semantic must remain aligned between desktop/game and standalone Android.

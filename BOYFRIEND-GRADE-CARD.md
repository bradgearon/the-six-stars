# Boyfriend grade card

Rebuild both the desktop host and `android/six-stars-mobile`. Tap the selected girlfriend's sprite to open the grade card. Existing cute-mode, breakup and posting controls remain below it. This change targets the host-connected Android app; it does not add a separate standalone-Android scoring engine.

Eight categories: Affection; Comfort & reassurance; Listening & attention; Reliability; Romance; Play & shared time; Respect & space; Care & support.

Each category shows a large cumulative grade, a smaller current-week grade, a dated remembered interaction and a current high-priority need/want when available. Weeks start Monday at 00:00 UTC, shown in the card's note. Three qualifying observations are required in each period; otherwise the category says “Not yet rated.”

## Evidence and calculation

The existing background interaction review now also emits `boyfriendPerformanceVector`. It uses the player's actions, credible reports, remembered activities, recent fulfillment, and ongoing needs/wants. The strict response schemas, compact vector decoder, response model, and memory update route all carry this new field. No additional model request is needed.

The prompt asks for one assessment per relevant category, with a concrete reason. It distinguishes the player's contribution from affection initiated solely by the Star, unfulfilled promises and repeated recollections of an already-scored event. Unknown categories, absent evidence and confidence below 0.5 do not earn grades.

Performance scores: 95–100 exceptional; 85–94 thoughtful; 75–84 good everyday care; 65–74 partial response; 50–64 missed communicated opportunity; below 50 explicit hurtful dismissal, broken commitment or boundary violation. The corresponding grades are S, A, B, C, D and F. Small welcome gestures can be good care; high grades never require escalating physical or sexual intimacy.

Each observation is weighted by confidence and by the most recent desired-state snapshot at or before the original turn. Weight = confidence × (1 + 1.5 × need priority + want priority), with priorities normalized to 0–1 and multiplied by their own confidence. A category grade is the weighted mean of its observations. Missing an expressed high-priority need therefore matters more, as does responding thoughtfully to it. Generated private wants and time offline never automatically subtract points. These remain model assessments, so the displayed reason makes them reviewable.

The persisted `StarState.BoyfriendGradeLedger` stores cumulative sufficient statistics, daily buckets for 90 days, and recent evidence IDs. Cumulative totals survive memory pruning and restarts; weekly grades use only that week's daily buckets. Replayed reviews for the same turn do not add another observation. A foreground update with no evidence does not consume the later background review. Relationship reset/breakup clears the ledger.

For older saves, retained fulfilled-need/want history is imported once on the next state update (and previewed before that). Its positive strength is mapped to 70 + 0.3 × strength, so a small welcome act does not become a failing grade. Old couple-activity records alone cannot reliably say who performed the action, so they are not retroactively credited to the player. They still inform the AI's future assessments. The card labels the earliest retained qualifying memory; it does not claim to reconstruct already-pruned lifetime history.

## Proposal follow-up

Proposal decisions now bypass the ordinary 30-second post-foreground delay and player-activity suppression. They cannot be discarded by foreground/activity preemption. A delivery ID ties the thinking phrase to its answer: desktop playback completion and mobile transport/audio completion (or failure) release the generated follow-up. Older hosts that do not signal completion have a two-minute compatibility timeout.

The desktop follow-up does not reuse the cancelled token of the initial request. The existing mobile proposal event appends the answer to the conversation and starts its own voice stream after the acknowledgement finishes. The voice conversation loop does not restart recording between the thinking phrase and answer. A generated response with empty speech receives a short decision-specific spoken fallback matching the persisted relationship gate.

This is live delivery, not a new offline audio mailbox: if the app is shut down or the phone has no audio connection, narration cannot be guaranteed. Existing text events and connection error reporting still apply.

## Verification

- Solution build with .NET SDK 9: successful, no errors; existing nullable/compatibility warnings remain.
- `dotnet run --project tests/BoyfriendGradeChecks/BoyfriendGradeChecks.csproj`: passes grade calibration, weekly boundaries, confidence filtering, priority weighting, replay protection, JSON persistence, historical import/attribution, compact model decoding and strict schema coverage.
- Actual `StarMemoryApi` foreground/background replay integration passes.
- Actual `SixStarsRuntime` proposal test passes acknowledgement ordering, cancellation of the original request, background cancellation attempts, nonempty acceptance fallback and persisted girlfriend state.
- Android MainActivity parses successfully with the Java syntax parser. Android SDK/Gradle build, visual layout on a device, and end-to-end TTS playback were not run here.

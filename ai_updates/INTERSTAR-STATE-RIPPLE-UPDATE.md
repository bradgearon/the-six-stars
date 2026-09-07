# Interstar State Ripple Update

This update adds a delayed interstar emotional-state pipeline after normal Star conversations.

## Flow

1. The selected Star conversation starts.
2. The foreground spoken reply finishes and the UI/API receives a `StarConversationForegroundCompleted` event.
3. The primary background emotion/memory review finishes.
4. If the turn produced usable emotion vectors or top affected emotions, an interstar ripple is queued.
5. The ripple waits for the conversation to settle. Default: 60 seconds.
   - Override for local testing with `SIX_STARS_INTERSTAR_SETTLE_SECONDS`.
   - Values are clamped between 5 and 600 seconds.
6. The origin Star privately decides what she would tell each eligible affected Star.
7. Each affected Star generates a compact second-hand emotional impression about the Last Cat plus a separate vector toward the source Star.
8. The second-hand Last Cat impression is awareness only: it does not change Last Cat Heart/Trust/Romance/connection and does not enter direct-player vector history. The source-Star vector may update the separate Star-to-Star relationship state.
9. The origin Star receives a final feedback-vector pass based on the other Stars' responses; its Last Cat impression is likewise awareness only while per-Star feedback vectors may update Star-to-Star relationships.

## API / runtime events

The runtime now exposes interstar lifecycle events:

- `InterstarUpdateStatusChanged`
- `InterstarMicroUpdateCompleted`
- `InterstarUpdateCycleCompleted`
- `InterstarUpdateFailed`

Status phases include:

- `ConversationStarted`
- `ForegroundCompleted`
- `PrimaryBackgroundCompleted`
- `Queued`
- `NotQueued`
- `Started`
- `RelayStarted`
- `Skipped`
- `ReactionStarted`
- `ReactionCompleted`
- `FeedbackStarted`
- `FeedbackCompleted`
- `Completed`
- `Cancelled`
- `Failed`

These are also surfaced as mobile bridge events using event types like `InterstarUpdateQueued`, `InterstarUpdateReactionStarted`, and `InterstarUpdateCompleted`.

## Star JSON configuration

Every Star profile now has `interstarRules`.

Each rule controls whether one origin Star can update a target Star:

- `targetStarId`
- `enabled`
- `requiresOriginAlly`
- `requiresTargetMet`
- `requiresTargetAlly`
- `minOriginConnectionPercent`
- `minTargetConnectionPercent`
- `relayWeight`
- `positiveMultiplier`
- `negativeMultiplier`
- `cooldownSeconds`
- `maxPerCycle`
- `topicTags`

The current implementation uses eligibility and cooldown deterministically, and passes rule weight/tag context into the relay prompt so the origin Star can decide whether the relay matters.

## UI changes

Desktop:

- Adds an `Interstar state` line in the runtime status / daily-state area.
- Publishes interstar status and micro-update events to the Android bridge.

Android:

- Adds an `Interstar status` panel.
- Handles `InterstarUpdate*` bridge events.
- Handles `InterstarEmotionUpdateSettled` events so interstar micro-updates can refresh the same emotion-vector display used by normal background updates.

## Deterministic state application

Interstar micro-updates are synthetic internal requests with `interstarMicroUpdate=true` so they do not recursively trigger another interstar ripple.

Interstar micro requests are explicitly isolated from the direct Last Cat relationship calculator. Their Last Cat impression vectors are retained for private awareness/debugging only and cannot move player Heart/Trust/Romance/EmotionalConnectionPercent or direct-player vector history. Separate source-Star/per-Star vectors still use deterministic code to update `OtherStars` Star-to-Star Heart/Trust state.

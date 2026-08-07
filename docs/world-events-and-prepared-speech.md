# World Events, Star Intentions, and Prepared Speech

This feature connects The Last Cat, Bradmageddon, father-form feeds, daily Star state, and the Android companion app. World events are stored locally, affected Stars refresh their state after a short debounce, and important wants-to-say items are converted into durable speech intentions with prepared Qwen audio.

## Data flow

```text
Game / Bradmageddon / father-form feed
        -> durable world-event inbox
        -> affected Star state refresh
        -> structured wants-to-say intentions
        -> deterministic priority + score aging
        -> background TTS artifact generation
        -> compact WebSocket ready event
        -> Android manifest reconciliation + verified download
        -> playback completion acknowledgement
```

WebSocket notifications are hints only. The HTTP manifest is authoritative, so reconnecting Android clients recover anything they missed.

## World-event API

The LAN bridge defaults to `http://192.168.1.8:8066`.

### Add one event

`POST /api/world-events`

```json
{
  "eventId": "settlement-seen-00042",
  "source": "the-last-cat-game",
  "sourceEventId": "save-4-event-810",
  "kind": "location_discovered",
  "occurredAt": "2026-07-27T16:00:00Z",
  "importance": 82,
  "authority": 0,
  "scope": "world",
  "targetStars": ["Elusiaa", "Kaelia"],
  "location": "Goblin Settlement",
  "summary": "The Last Cat has seen Elusiaa and Kaelia's settlement.",
  "details": "The goblins are using captive slimes in the poisoned water system.",
  "requiresStateRefresh": true,
  "metadata": {
    "settlementId": "goblin-settlement-one",
    "playerWitnessed": "true"
  }
}
```

### Add a batch

`POST /api/world-events/batch`

The body can be a JSON array or an object containing an `events` array. Duplicate delivery is safe: `source + sourceEventId` is the idempotency key. Remote feed items that omit both IDs receive a stable SHA-256 identity.

### Read events

`GET /api/world-events?afterSequence=123&max=100&starId=Elusiaa`

### Force state refresh

- `POST /api/star-state/refresh`
- `POST /api/star-state/refresh/Elusiaa`

Optional body:

```json
{
  "sessionId": "local-dev",
  "starIds": ["Elusiaa", "Kaelia"],
  "reason": "Settlement event stream updated"
}
```

When an accepted event has `requiresStateRefresh=true`, the runtime already queues a debounced refresh for the targeted Stars. No target means all Stars.

## Feed configuration

In `src/Wds.SixStars.Ui/appsettings.json`:

```json
"WorldEvents": {
  "Enabled": true,
  "StateRoot": "state/world-events",
  "MaximumStoredEvents": 2000,
  "PromptEventCount": 32,
  "RefreshDebounceSeconds": 8,
  "ApiKey": "",
  "Feeds": [
    {
      "FeedId": "bradmageddon",
      "Url": "https://example.invalid/bradmageddon/events",
      "Enabled": true,
      "PollSeconds": 20,
      "ApiKey": ""
    },
    {
      "FeedId": "father-forms",
      "Url": "https://example.invalid/father-forms/events",
      "Enabled": true,
      "PollSeconds": 15,
      "ApiKey": ""
    }
  ]
}
```

A configured key is sent as `Authorization: Bearer <key>`. The local POST API accepts the same bearer header or `X-Six-Stars-Key` when `WorldEvents.ApiKey` is set.

## Father-form behavior

Father-form messages are presented as powerful paternal and spiritual authority, not infallible commands. A Star is strongly inclined to support them, while attributes, mercy, evidence, relationship state, and accumulated experience can produce hesitation, reinterpretation, resistance, or refusal. Event importance and authority also create deterministic score/priority floors so a major directive cannot be silently reduced to an unimportant thought.

## Durable speech intentions

Daily-state output now includes `thingsSheWantsToSay` entries with:

- `intentId`
- `category`
- `topic`
- `speechGoal`
- `baseScore`
- `priority`: `low`, `medium`, `high`, or `critical`
- `confidence`
- `sourceEventIds`
- `dedupeKey`
- expiration and acknowledgement settings

The backend calculates `effectiveScore` from base score, priority, age, and retry penalties. Important thoughts rise over time. Spoken/dismissed items remain in a compact archive and exact duplicates are suppressed for `MinimumRepeatHours`, while changed goals or new source events can create a fresh intention.

Lifecycle:

```text
pending -> preparing -> ready -> leased -> playing -> spoken
                   \-> failed
pending/ready -> dismissed / expired / superseded
```

An Android crash does not lose a thought. A lease expires and returns the item to `ready` or `pending`.

## Prepared-speech API

- `GET /api/prepared-speech/manifest?starId=Elusiaa`
- `GET /api/prepared-speech/{artifactId}/chunks/{chunkIndex}`
- `POST /api/prepared-speech/{intentId}/lease`
- `POST /api/prepared-speech/{intentId}/started`
- `POST /api/prepared-speech/{intentId}/completed`
- `POST /api/prepared-speech/{intentId}/dismissed`

The manifest includes active intentions, ready artifacts, highest score, highest priority, and the Android playback policy (score thresholds, page dwell, relationship probabilities, cooldown, and speak-before-reply limits). Audio chunks include byte length and SHA-256. Android downloads to `.part`, validates length/hash, then atomically renames the file.

The server stores prepared audio under:

```text
state/prepared-star-speech/<artifactId>/
    metadata.json
    chunk-000.wav
    chunk-001.wav
```

Android stores each intention independently under its private app files directory. It reconciles on app open, resume, WebSocket reconnect, ready events, and periodically while running.

## Android behavior

The existing **Let the selected Star speak at rest** checkbox gates all autonomous playback.

- High/critical thoughts can speak after the Star page remains visible briefly.
- Medium thoughts can speak at rest when she is the girlfriend or connection is high.
- The page shows whether an important thought is preparing or downloaded and ready.
- Tapping the indicator plays it immediately.
- Before text, voice, or photo replies, an eligible prepared thought can sometimes play first while the foreground LLM request runs concurrently.
- Prepared chunks are inserted ahead of incoming desktop reply chunks, preventing a fast reply from overtaking the thought.
- The item is removed only after the final audio chunk completes and the server acknowledges `completed`. Android first persists a `completionPending` marker; reconnect reconciliation retries the acknowledgement without replaying audio after a transient network failure or app restart.

## Pause and resume

Android playback now separates pause/resume from destructive queue clearing. Pause preserves the current `AudioTrack` position and all queued speech. Resume continues the same audio.

A game or control client can dispatch:

```json
{
  "commandType": "SetSpeechPlaybackPaused",
  "playbackPaused": true,
  "reason": "Game paused"
}
```

Send `playbackPaused:false` or `commandType:"ResumeSpeech"` to resume.

## Foreground response length

`ForegroundSpeechStyle.ConciseModeProbability` controls a per-turn host-side roll. When selected, ordinary responses are instructed to use 3-5 natural TTS chunks. Explicit requests for a longer story, a specified number of jokes, detailed explanation, or larger list override the limit. The roll and selected mode are written into request flags for logging and diagnosis.

## Main configuration knobs

```json
"StarSpeechIntents": {
  "AgingPointsPerHour": 1.5,
  "MaximumAgeBoost": 18,
  "HighPriorityPreparationThreshold": 75,
  "MediumPriorityPreparationThreshold": 55,
  "MaximumPreparedItemsPerStar": 2,
  "MaximumPrepareAttempts": 5,
  "PrepareRetryDelaySeconds": 15,
  "PreparationTimeoutMinutes": 10,
  "LeaseMinutes": 5,
  "SpokenArchiveDays": 30,
  "MinimumRepeatHours": 12
},
"ForegroundSpeechStyle": {
  "ConciseModeProbability": 0.5,
  "ConciseMinimumChunks": 3,
  "ConciseMaximumChunks": 5,
  "RespectExplicitLengthRequests": true
}
```

## Settlement example

1. The game posts that the Last Cat saw the goblin settlement.
2. The father-form feed posts a high-authority destruction directive.
3. Bradmageddon can post an additional warning or opinion.
4. Elusiaa is marked dirty and refreshed after the debounce.
5. Her attributes and event context produce a high-priority intention such as explaining that she intends to destroy the settlement.
6. Qwen prepares the speech and Android downloads it before it is needed.
7. Opening Elusiaa's page lets her raise the issue naturally.
8. Conversation changes her attributes and later state refreshes can supersede an unspoken intention or generate a new one.


### Preparation timeout recovery

`StarSpeechIntents.PreparationTimeoutMinutes` defaults to `10`. If the desktop or TTS process stops while an intent is `preparing`, the durable queue marks it failed after the timeout and retries it according to `PrepareRetryDelaySeconds` and `MaximumPrepareAttempts`.

# Star Writing X Posting

## Purpose

The desktop can let each Star select five of her own persisted shelf writings and publish them to her configured X account. Public posting is separate from telling writings aloud: `ToldCount` and `PostedAt` do not affect one another.

## Selection and posting flow

1. Load the selected Star's persisted writing shelf.
2. Keep only writings with no `PostedAt` value.
3. Randomize the unposted shelf and send at most 500 candidates to a dedicated Scriban LLM request.
4. Include the Star's identity, element, domain, personality, backstory, speaking style, voice direction, attributes, and behaviors.
5. Require exactly five distinct source writing IDs and one final public post text for each.
6. Post the five items in order with the configured delay between them.
7. Set `PostedAt`, `XPostId`, and `PostedText` only after the X API accepts that individual post.

A failed post remains unposted and can be selected on a later run. Whether a writing has been spoken has no effect on X eligibility.

## Uninterrupted batches

The X scheduler and publisher are independent from the interruptible background writing loop. Player speech, foreground LLM requests, recording, Star selection changes, runtime cancellation, and disabling the schedule do not cancel a five-post batch after it has started. Disabling the feature prevents future scheduled batches.

## Schedule

The default schedule uses the same four local-time windows as daily Star state:

- overnight: 00:00–06:00
- morning: 06:00–12:00
- afternoon: 12:00–18:00
- evening: 18:00–00:00

Each successful five-attempt batch is recorded per local date, slot, and Star in `state/x-posting/schedule-state.json`, preventing duplicate execution in the same slot. A batch that cannot start, such as one with fewer than five unposted writings or missing credentials, remains eligible for retry after `RetryDelaySeconds`.

## Configuration

Keep real credentials in `src/Wds.SixStars.Ui/appsettings.Local.json`, which is ignored by Git. Do not commit them.

```json
"XPosting": {
  "Enabled": false,
  "StateRoot": "state/x-posting",
  "CandidateLimit": 500,
  "DelayBetweenPostsSeconds": 120,
  "RequestTimeoutSeconds": 45,
  "PollSeconds": 60,
  "RetryDelaySeconds": 300,
  "RefreshSlots": [
    { "SlotId": "overnight", "StartsAt": "00:00", "RefreshBefore": "06:00" },
    { "SlotId": "morning", "StartsAt": "06:00", "RefreshBefore": "12:00" },
    { "SlotId": "afternoon", "StartsAt": "12:00", "RefreshBefore": "18:00" },
    { "SlotId": "evening", "StartsAt": "18:00", "RefreshBefore": "00:00" }
  ],
  "StarAccounts": [
    {
      "Id": "elusiaa",
      "Enabled": true,
      "ApiKey": "",
      "ApiSecret": "",
      "AccessToken": "",
      "AccessTokenSecret": ""
    }
  ]
}
```

The account ID is the lower-case Star ID. The shipped examples include `elusiaa`, `kaelia`, `maera`, `khavia`, `auerlis`, and `nyxari`. Accounts can be disabled individually.

Optional environment overrides:

- `SIX_STARS_X_POSTING_ENABLED`
- `SIX_STARS_X_POSTING_DELAY_SECONDS`
- `SIX_STARS_X_POSTING_REQUEST_TIMEOUT_SECONDS`
- `SIX_STARS_X_POSTING_POLL_SECONDS`
- `SIX_STARS_X_POSTING_RETRY_DELAY_SECONDS`
- `SIX_STARS_X_<STAR>_API_KEY`
- `SIX_STARS_X_<STAR>_API_SECRET`
- `SIX_STARS_X_<STAR>_ACCESS_TOKEN`
- `SIX_STARS_X_<STAR>_ACCESS_TOKEN_SECRET`

For Elusiaa, `<STAR>` is `ELUSIAA`.

## Controls

The desktop Writing Shelf panel includes:

- a toggle for four-times-daily posting;
- **Post Selected Star to X Now**;
- current X posting status.

The Android player popup contains the same toggle and immediate-post button below the Writing Shelf controls. The immediate command queues work on the desktop and returns promptly; progress arrives through bridge events.

## X transport

The API layer uses X API v2 `POST /2/tweets` with OAuth 1.0a user-context credentials. The project uses a small `HttpClient` REST implementation because the application is C# and the X integration is limited to creating text posts.

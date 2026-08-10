# Star writing X posting

The writing shelf can publish five Star-selected writings to X at each of the four daily state windows or from the desktop **Post Selected Star to X Now** button.

## Selection and posting behavior

1. Load persisted shelf writings that do not have `PostedAt`, have non-empty text, and fit X's 280-character limit.
2. Rank the eligible shelf deterministically from each writing's saved 27-value review vector. Public ranking is 40% `Overall`, 40% the average of durable craft/retellability dimensions (`Memorable`, `Original`, `Coherent`, `Meaningful`, `Characterful`, `EmotionallyResonant`, `Polished`, `Quotable`, `WorthRetelling`), and 20% the average of that writing's three strongest expressive dimensions. Writings that matched multiple non-global persistence routes receive a small bonus.
3. Take the strongest `CandidateLimit` writings (maximum 500), strongest first. There is no random shelf sampling in this stage.
4. Ask the selected Star, through the dedicated Scriban templates, to choose exactly five favorites from that already-high-quality window. The selected persisted writing text is returned unchanged; no hashtags or extra copy are added.
5. Post the five items in sequence through X API v2 `POST /2/tweets`.
6. Wait `DelayBetweenPostsSeconds` between attempts.
7. Set `PostedAt`, `XPostId`, and `PostedText` only after X accepts that individual post.

The desktop status text updates throughout a running batch: selection complete, posting `N of 5`, success/failure of each attempt, and the configured wait before the next post. Progress reporting is best-effort and cannot interrupt the batch if the UI/status callback fails.

Spoken/told state is independent from posted state. A writing remains eligible until its post succeeds.

## OAuth 2.0 desktop authorization

The desktop uses X OAuth 2.0 Authorization Code with PKCE. Tokens are not pasted into JSON.

For each X developer app:

1. Enable OAuth 2.0 user authentication in the X Developer Console.
2. Choose **Native App** so the desktop is treated as a public client and does not embed a client secret.
3. Register the callback exactly as configured, by default:
   `http://127.0.0.1:47837/x-oauth2/callback/`
4. Copy the OAuth 2.0 **Client ID** into the matching lower-case Star account entry.
5. Select that Star in the desktop and click **Authorize / Reauthorize Selected Star for X**.
6. Sign into the correct Star X account in the browser and approve access.

The desktop temporarily listens only on the configured loopback address and port. It generates a one-use PKCE verifier/challenge and CSRF `state`, opens the X authorization page, validates the local callback, exchanges the authorization code immediately, and stores the resulting access and refresh tokens encrypted with Windows DPAPI for the current user.

The requested scopes are:

- `tweet.read`
- `tweet.write`
- `users.read`
- `offline.access`

`offline.access` is required for a refresh token. Access tokens are refreshed automatically before expiration and the rotated refresh token is saved immediately.

## Configuration

```json
"XPosting": {
  "Enabled": false,
  "StateRoot": "state/x-posting",
  "CandidateLimit": 500,
  "DelayBetweenPostsSeconds": 120,
  "RequestTimeoutSeconds": 45,
  "PollSeconds": 60,
  "RetryDelaySeconds": 300,
  "OAuthAuthorizationTimeoutSeconds": 180,
  "OAuthTokenRefreshSkewSeconds": 120,
  "OAuthScopes": "tweet.read tweet.write users.read offline.access",
  "StarAccounts": [
    {
      "Id": "elusiaa",
      "Enabled": true,
      "ClientId": "PASTE_X_OAUTH2_CLIENT_ID",
      "RedirectUri": "http://127.0.0.1:47837/x-oauth2/callback/"
    }
  ]
}
```

Per-Star environment overrides:

- `SIX_STARS_X_ELUSIAA_CLIENT_ID`
- `SIX_STARS_X_ELUSIAA_REDIRECT_URI`

Replace `ELUSIAA` with the upper-case Star id.

Global overrides:

- `SIX_STARS_X_POSTING_ENABLED`
- `SIX_STARS_X_POSTING_DELAY_SECONDS`
- `SIX_STARS_X_POSTING_REQUEST_TIMEOUT_SECONDS`
- `SIX_STARS_X_POSTING_POLL_SECONDS`
- `SIX_STARS_X_POSTING_RETRY_DELAY_SECONDS`
- `SIX_STARS_X_OAUTH_TIMEOUT_SECONDS`
- `SIX_STARS_X_OAUTH_REFRESH_SKEW_SECONDS`
- `SIX_STARS_X_OAUTH_SCOPES`

Encrypted token files are stored below `state/x-posting/oauth2-tokens`. They can only be decrypted by the same Windows user on the same Windows installation. Reauthorizing replaces that Star's saved token.

## Scheduling

Each successful five-attempt batch is recorded per local date, slot, and Star in `state/x-posting/schedule-state.json`, preventing duplicate execution in the same slot. A batch that cannot start, such as one with fewer than five unposted writings or no authorization, remains eligible for retry after `RetryDelaySeconds`.

A started batch is deliberately independent from foreground conversation cancellation. Fully closing or killing the desktop process still ends in-process work.

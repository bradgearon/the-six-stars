# X posting: 403 permission diagnostics and batch protection

- A create-post `401 Unauthorized` still forces one OAuth refresh and retries once.
- A create-post `403 Forbidden` now also forces exactly one refresh/retry so a stale authorization state gets one chance to recover.
- If the refreshed token still receives `403`, the client calls `GET /2/users/me` with that same token.
  - If `/2/users/me` succeeds, OAuth is valid and the error is reported as an X-side create-post/account/app permission denial, including the authenticated username when X returns it.
  - If `/2/users/me` fails too, the status asks for reauthorization because the refreshed token itself could not be validated.
- The refresh-token parser preserves the previous scope string when a refresh response omits `scope`.
- A confirmed `403` stops the remainder of the current five-post batch immediately instead of waiting two minutes and sending four more requests that are expected to fail.
- Scheduled posting treats a hard `403` as handled for the current schedule slot, preventing the background loop from retrying the same blocked account every few minutes. The next normal slot may try again, and manual posting can still be used for an immediate test.
- Successfully published posts before a mid-batch `403` remain marked posted; unattempted selected writings remain unposted and eligible later.

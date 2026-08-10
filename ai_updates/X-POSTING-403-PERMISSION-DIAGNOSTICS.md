# X posting: 403 permission diagnostics and batch protection

- A create-post `401 Unauthorized` still forces one OAuth refresh and retries the post exactly once.
- A create-post `403 Forbidden` no longer refreshes the token or retries the rejected post. It is treated as a hard create-post denial immediately.
- On `403`, the client calls `GET /2/users/me` with the same currently authorized user token.
  - If `/2/users/me` succeeds, OAuth is valid and the error is reported as an X-side create-post/account/app permission denial, including the authenticated username when X returns it.
  - If `/2/users/me` fails too, the status asks for reauthorization because the current token itself could not be validated.
- If a post initially returns `401`, the one allowed refresh/retry can still reveal a `403`; that `403` is diagnosed but is not refreshed or posted again.
- The refresh-token parser preserves the previous scope string when a refresh response omits `scope`.
- A `403` stops the remainder of the current five-post batch immediately instead of waiting two minutes and sending more create-post requests that are expected to fail.
- Scheduled posting treats a hard `403` as handled for the current schedule slot, preventing the background loop from retrying the same blocked account every few minutes. The next normal slot may try again, and manual posting can still be used for a later test after the account restriction is resolved.
- Successfully published posts before a mid-batch `403` remain marked posted; unattempted selected writings remain unposted and eligible later.

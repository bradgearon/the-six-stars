# Android fast foreground and Star initiative path

This change removes several avoidable delays that only showed up strongly in the Android session.

## Foreground text, voice, and photo requests

- Android foreground requests now use a queue separate from Star initiative checks. A slow initiative request can no longer sit in front of a player message inside the phone app.
- The initiative queue has two workers so a user-pressed Listen request can reach the desktop even while an automatic at-rest HTTP call is still pending. The desktop treats manual Listen as a priority request and cancels/preempts the older automatic check.
- When a foreground request reaches the desktop bridge, it continues to preempt/cancel at-rest and background runtime work.
- A missing or stale daily Star state no longer causes a synchronous daily-state LLM generation before the actual player reply. The newest cached state is used immediately and refresh generation is queued at low priority for a later turn.
- Information-needs routing no longer spends a second LLM call selecting an item from the vector the foreground model already produced. Deterministic local availability and thresholds make that selection; a deep follow-up still runs only when genuinely needed.

## Ask whether she wants to speak

A manual Listen request is treated as foreground work by the desktop dispatcher, while automatic initiative remains low priority. Initiative also reads only existing daily/girlfriend state caches and never starts those larger state-generation calls where they could compete with its own LLM call.

StarInitiative now has its own compact prompt and compact JSON schema. It receives only the relationship/state facts, a few daily or girlfriend hooks, and three recent turns. It returns either no speech or one short line. The request has a dedicated 256-token output ceiling, configurable with `SIX_STARS_LLM_INITIATIVE_MAX_TOKENS` (`0` disables the cap).

## Timing visibility

Android response debug now shows:

- server total milliseconds
- runtime milliseconds
- time waiting for the mobile runtime gate

This makes it possible to distinguish model-generation time from queue contention or bridge overhead on the actual machine.

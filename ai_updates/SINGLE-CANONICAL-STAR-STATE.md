# Single canonical Star state

The desktop and Android clients now share one durable Star relationship/memory save:

`state/six-stars-local-dev-state.json`

`SessionId` remains on transport/request models for correlation and logging compatibility, but it no longer selects a different relationship state.

On the first load after this update, the JSON store reads the old root mirror, `state/sessions/*.json`, and legacy per-session Star shards. For each Star it keeps the richest established state, preferring real interaction history over a newer blank/default session. It then writes save version 2 to the canonical root file. Future reads and writes ignore the incoming session id.

Daily generated Star state is likewise stored by Star rather than by client session.

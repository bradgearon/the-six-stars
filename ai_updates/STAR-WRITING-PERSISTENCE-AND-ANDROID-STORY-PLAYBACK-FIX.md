# Star writing persistence + Android Story playback fix

## Symptoms

- New ReaderEmotionIuV1 writing was reviewed and recorded in recent history, but every item stayed `Persisted=false`.
- Android **Listen -> Story** could therefore return the empty-shelf response and had no narration to play.
- If a persisted performance did exist, a stale/disconnected phone-audio TCP socket could still let text return while no WAV stream reached Android.

## Root causes

1. Persistence thresholds were still calibrated for the older writing-value distribution. The authored quality gates commonly required two supporting emotions at 60+ and the shared Overall fallback required strictly greater than 80. In the 2026-08-14 unified ReaderEmotion export, none of 325 reviewed items persisted.
2. The Android writing-shelf dispatch path no longer refreshed the independent phone-audio socket before `TellStarWriting`.
3. Desktop voice delivery snapshotted audio clients immediately, so a reconnect started by the button could lose the race.

## Persistence policy v1

Reader review remains one canonical compact 31-position `reader-emotion-iu-v1` vector. No per-emotion JSON review format was reintroduced.

Spark persistence keeps the existing authored Star-specific gates/rules, plus an independent Overall-IU top-shelf route calibrated to the first 310 unified-IU Sparks:

- Auerlis: `Overall >= 65`
- Elusiaa: `Overall >= 40`
- Kaelia: `Overall >= 65`
- Khavia: `Overall >= 55`
- Maera: `Overall >= 55`
- Nyxari: `Overall >= 65`

That reclassifies 20 / 310 Sparks (6.45%), distributed across all six Stars, close to the earlier ~7.7% shelf-selectivity target instead of zero.

Constellation Stories use an independent `Overall >= 55` backstop. In the same export that reclassifies 13 / 15 stories and gives every Star at least one playable historical Story.

The comparison is inclusive (`>=`). Existing Star-specific emotional rules remain available as additional routes.

Environment overrides:

- `WDS_STAR_WRITING_SPARK_OVERALL_VALUE_THRESHOLD`
- `WDS_STAR_WRITING_SPARK_OVERALL_VALUE_THRESHOLD_<STAR>`
- `WDS_STAR_WRITING_CONSTELLATION_OVERALL_VALUE_THRESHOLD`
- `WDS_STAR_WRITING_CONSTELLATION_OVERALL_VALUE_THRESHOLD_<STAR>`
- legacy `WDS_STAR_WRITING_GLOBAL_OVERALL_VALUE_THRESHOLD` / `...GLOBAL_OVERALL_THRESHOLD` remain compatible fallbacks.

## One-time local shelf recovery

`StarWritingNotesState.PersistencePolicyVersion` now tracks deterministic shelf-policy migrations.

On the first state load after this update, each Star replays only its existing append-only `all-star-writing-statements.jsonl` ReaderEmotion decisions through the current persistence policy. Qualifying historical entries are added to `PersistedWritings` without another LLM call and without rewriting the append-only log.

Clearing a Star remains safe: a freshly cleared/missing state starts at the current persistence-policy version, so old append-only log entries are **not** resurrected after an intentional clear.

## Android Story / Spark voice

Before a foreground `TellStarWriting` request with phone audio enabled, Android now:

- pauses at-rest initiative preparation,
- keeps the screen awake through the response handoff,
- immediately reconnects the phone voice socket to the active bridge route,
- refreshes `/health` so a custom audio port or promoted IPv6 route can replace the default route.

Desktop voice delivery now waits event-driven for up to six seconds for an Android audio client before creating the new voice stream and snapshotting its target clients. Text still returns before TTS playback.

This restores the intended behavior documented by the earlier Android writing-shelf voice fix while also fixing the empty Story shelf that prevented narration in the first place.

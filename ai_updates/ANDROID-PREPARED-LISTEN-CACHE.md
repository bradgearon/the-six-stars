# Android Prepared Listen Cache

The Android Listen button now prefers a privately prepared at-rest response instead of waiting for a new LLM and TTS round trip.

## Flow

1. When Android is quiet, `StarInitiativeBackgroundService` requests `MaybeStarInitiative` for the selected Star with `prefetchForListen=true` and `returnAudioInline=true`.
2. The desktop runtime queues that request at low background priority and begins it only while no foreground runtime request is active.
3. The desktop generates the Star's at-rest text and ordered TTS WAV chunks, then returns them only to the requesting phone. The response is not broadcast into the conversation UI before Listen is pressed.
4. Android stores one prepared item under its private app files and loads its WAV bytes into memory while the activity is alive.
5. The first Listen tap consumes the item, immediately updates the conversation window, and queues the already-generated WAV chunks for playback.
6. Once playback and the live voice stream are idle, Android schedules preparation of the next item.

## Efficiency and correctness

- There is only one cached response for the current session and selected Star.
- A lightweight metadata/file check avoids repeatedly loading WAV data in the background service.
- A ready cache is checked only every 25-40 minutes; consuming it explicitly requests the next one.
- Foreground text, voice, photo requests and Star changes invalidate stale cache material.
- A generation number prevents an in-flight stale prefetch from being saved after the player speaks, changes Stars, or consumes a previous item.
- A shared `notBefore` timestamp prevents preparation while a foreground request, desktop voice stream, or Android audio playback is active.
- Prepared initiative requests do not use the shared foreground cancellation operation, so they cannot cancel a desktop or Android player reply.
- The runtime routes prefetch through `BackgroundAiRequestDispatcher` at high background priority, while every foreground request still blocks it.
- `RequestType.StarInitiative` does not apply relationship deltas, emotion scoring, memory updates, or interstar ripple, so preparing a line does not pretend the player already heard it.

## Persistence

The activity keeps the prepared WAV chunks in memory for immediate playback. A disk-backed copy survives normal activity/process recreation, because Android cannot guarantee that app memory or a background service remains alive while the device sleeps. Prepared items expire after two hours.

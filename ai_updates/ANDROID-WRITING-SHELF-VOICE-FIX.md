# Android persisted-writing voice fix

The Android **Tell persisted writing** action could display the selected text without playing the Star voice when the separate desktop-to-phone audio socket was disconnected, stale, or still reconnecting.

## Changes

- The Android app now treats a persisted-writing performance as a foreground interaction.
- Before sending `TellStarWriting` with phone audio enabled, Android refreshes an idle audio socket and reconnects it to the active bridge audio port.
- Prepared/at-rest initiative is paused so another request cannot replace the writing performance while its TTS stream is starting.
- The phone stays awake through the text response and resumes background initiative after the performance window.
- The desktop now gives a reconnecting Android audio client up to six seconds to join before it snapshots the target clients for a new voice stream.
- The desktop wait is event-driven through the audio host's connection status event; it does not add periodic polling.

The text response is still sent first. Voice generation remains deferred until after the correlated bridge response has reached the phone.

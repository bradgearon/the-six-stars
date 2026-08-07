# Android keep screen on while Star is answering

Updated the Android mobile client so the phone display stays awake while a Star is actively answering.

## Behavior

The app now sets `WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON` while any of these are active:

- a text/photo/voice request is waiting for the desktop response
- an Android phone voice stream has started and has not ended or been cancelled
- an audio chunk is actively playing through `AudioTrack`

The flag is cleared only after all active answer reasons have finished. This means the screen should stay on through Whisper transcription, the foreground LLM response, phone voice streaming, and playback.

## Files changed

- `android/six-stars-mobile/app/src/main/java/com/wds/sixstarsmobile/MainActivity.java`

## Notes

The implementation uses a small reason-mask so request waiting, stream generation, and playback cannot accidentally clear each other too early. The voice journal also logs when the app keeps the phone awake and when it releases the screen again.

# Android shirt-off request reaction

## Foreground speech

The fast spoken-response prompt now detects direct requests for a Star to show her breasts or remove her shirt/top. The Star answers in character that she is considering the request rather than ignoring it or replacing the answer with abstract dialogue.

## Background emotion review

The compact background response schema now requires:

```json
"requestToShowBoobs": false
```

The review instructions set it to `true` for a direct shirt-off / breast-exposure request. The runtime also performs a small deterministic phrase check so malformed or missed review output does not lose the event.

## Android reaction

The desktop publishes `requestToShowBoobs` with the settled background-emotion bridge event. Android then:

1. waits for the current desktop voice stream and queued WAV playback to finish;
2. looks for `star_animations/<star-id>/shirt-off.gif`;
3. plays it once without looping;
4. holds its final frame for two seconds;
5. returns to `idle.gif`.

If the optional asset is absent, Android leaves the normal animation active.

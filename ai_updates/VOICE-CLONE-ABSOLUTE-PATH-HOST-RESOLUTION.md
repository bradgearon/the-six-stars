# Voice Clone Absolute Path Host Resolution

Updated the voice-clone flow so the Python WebSocket TTS server no longer searches for clone files, guesses filenames, or falls back to Star JSON clone paths.

## New contract

The host is responsible for resolving the selected Star's voice clone file before sending the WebSocket request.

The request must include:

```json
{
  "voiceCloneAudio": "U:\\six-stars-solution\\src\\Wds.SixStars.Api\\Content\\VoiceClones\\elusiaa_clone.wav",
  "voiceCloneRefText": "exact reference transcript here"
}
```

## Desktop behavior

`MainViewModel` now resolves `VoiceDirective.VoiceCloneAudioFile` to a full path before it sends the response to streaming TTS or story prefetch TTS.

It checks the app output folder first, then common repo/source locations such as:

- `Content/VoiceClones/<file>`
- `src/Wds.SixStars.Api/Content/VoiceClones/<file>`

If the file cannot be found, it still sends the most likely absolute path so the Python error shows the exact missing file path.

## Python behavior

`scripts/websocket_tts_server.py` now validates only the request-provided path:

- empty path: error
- relative path: error
- missing file: error with exact path
- valid absolute path: use it directly

This makes Unity integration cleaner: Unity can pass its own full clone file path without the Python service knowing anything about Unity project layout.

# Native qwentts Smart PCM Rolling Buffer

Updated the direct qwentts.cpp PCM path so low-latency packets are no longer played as independent Android WAV clips.

## Playback path

- qwentts.cpp still returns raw 24 kHz mono PCM with `response_format=pcm`.
- .NET accumulates that stream into small transport packets (`PcmPacketMilliseconds`, default 240 ms) and WAV-wraps each packet only for compatibility with the existing desktop-to-phone frame format.
- The mobile frame header marks those packets with `continuousPcm=true`, their PCM duration, and a compact key/character count for the source text.
- Android strips the WAV framing and puts every packet from the same logical response into one `AudioTrack` instead of creating a new track per packet.

## Smart buffer

The phone player starts with a learned ~900 ms lead and continuously measures actual packet-arrival speed and jitter. It adapts the startup target between roughly 500 and 2200 ms:

- fast generation can reduce the learned lead over clean responses;
- near-real-time generation and jitter increase the lead;
- when generation is slower than real time, estimated remaining spoken duration contributes extra safety lead;
- an AudioTrack underrun or emergency low-water event raises the learned lead quickly;
- when the remaining lead falls below the emergency watermark, playback can briefly pause and refill instead of allowing an audible underrun.

Generation-rate measurements use the timestamp captured when each packet reaches the phone socket, not when the playback worker happens to consume it, so AudioTrack backpressure does not distort the estimate.

## Configuration cleanup

The active configuration now has one `QwenTTS` section. Removed obsolete `TTS` / `WebSocketTTS` blocks, duplicate Qwen fields from `LocalModels`, old first/steady PCM-window settings, unused chunk-generation mode/parallel settings, and the stale `MainViewModel.MobileBridge.cs.orig` backup.

Default active settings:

```json
"QwenTTS": {
  "Enabled": true,
  "HttpBaseUrl": "http://localhost:8101",
  "Model": "qwen3-tts-base",
  "DefaultLanguage": "English",
  "ResponseFormat": "pcm",
  "PcmPacketMilliseconds": 240,
  "TaskType": "VoiceClone"
}
```

`response_format=wav` remains supported as a compatibility/fallback mode.

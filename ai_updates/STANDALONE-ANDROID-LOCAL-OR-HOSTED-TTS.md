# Standalone Android local-or-hosted TTS update

## Purpose

Allow `android/six-stars-standalone` to use the exact local Qwen3-TTS WebSocket service already shipped with the desktop solution, while retaining Alibaba/DashScope and Cloudflare-proxied hosted TTS.

This is a transport/configuration change only. It does not alter Star profiles, prompt semantics, memory/state updates, deterministic relationship calculations, or the import compatibility contract.

## Canonical local service

The desktop solution already exposes Qwen3-TTS through:

- server: `scripts/websocket_tts_server.py`
- desktop client protocol: `src/Wds.SixStars.Api/Voice/WebSocketTtsVoiceGenerator.cs`
- route: `ws://<host>:8100/tts/stream`
- server default bind host: `0.0.0.0`

The standalone Android implementation intentionally copies that protocol rather than inventing a separate mobile TTS contract.

For each sentence the Android client sends a `generate` request containing the same core fields used by desktop: request id, text, Star id, voice id, model, language, WAV response format, VoiceClone task type, speed, clone audio filename, and clone reference text.

The server's existing binary frame is parsed as little-endian:

1. int32 chunk index
2. int32 total chunks
3. int32 UTF-8 text length + text
4. int32 Star id length + Star id
5. int32 voice id length + voice id
6. remaining bytes = WAV data

Android supplies the already sentence-sized text as a one-item `textParts` array so the local server does not split the sentence again and one Android TTS queue item corresponds to one complete WAV.

## Configuration

`TTS.Mode` is `auto`, `local`, or `hosted`.

- `local`: `LocalWebSocketUrl` is mandatory and hosted TTS is never used as a fallback.
- `hosted`: Alibaba/Cloudflare is used regardless of the local URL.
- `auto`: a non-empty local URL wins; otherwise hosted TTS is selected.

Local-specific fields:

- `LocalWebSocketUrl`
- `LocalModel`
- `LocalLanguage`
- `LocalTaskType`
- `LocalSpeed`
- `LocalTimeoutSeconds`

The default local URL is blank. This preserves the previous hosted behavior until a developer deliberately configures the local service.

## Cost-safety rule

A configured local TTS failure must fail visibly. Do not automatically fall back to hosted Alibaba after a connection failure, because that could silently create billable usage.

## Android networking

The repository's local server is plain `ws://`, therefore the standalone manifest permits cleartext traffic. This path is intended for a trusted LAN/developer setup. Google, Groq, Cloudflare and Alibaba remain HTTPS.

## Runtime tracing

TTS trace records now include the resolved client mode (`local-qwen-websocket` or `hosted-alibaba-qwen3-tts`) for start, generated audio and playback completion/failure events.

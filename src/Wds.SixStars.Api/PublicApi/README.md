# Wds.SixStars.Api public API surface

This folder gathers the pieces that an outside host normally calls or implements.
Namespaces are intentionally unchanged so existing code keeps working.

## Facades

- `SixStarsConversationApi` — UI/Unity-friendly facade for text, speech transcripts, screen summaries, Star state, history, and save import/export.

## Interfaces

These are host-swappable seams:

- `Ai/ILocalChatModelClient` — local Gemma/DeepSeek/OpenAI-compatible chat model client.
- `Audio/ISpeechActivityDetector` — voice activity detector.
- `Audio/IWakeWordDetector` — text wake alias detector.
- `Engine/IStarAgent` — Star brain implementation.
- `Engine/IRelationshipDeltaCalculator` — deterministic relationship delta rules.
- `Engine/IStarStateStore` — state persistence backend.
- `Screen/IScreenContextStore` — recent screen-summary store.
- `Screen/IScreenImageCaptureService` — host-provided screenshot capture source.
- `Screen/IScreenSummarizer` — local VLM/screen summarizer adapter.
- `Speech/IAudioHotwordDetector` — audio hotword model.
- `Speech/IWhisperSpeechStreamer` — local Whisper transcription adapter.
- `Stars/IStarProfileProvider` — Star JSON/profile provider.
- `State/IStarMemoryApi` — durable Star relationship/memory/history API.
- `Voice/IStarVoiceGenerator` — Qwen3-TTS or other Star voice model adapter.
- `Voice/IWavePlaybackEndpoint` — desktop/Unity WAV playback endpoint.
- `Voice/IStarSpeechPlaybackObserver` — self-listening prevention hook.

## Adapters

- `Speech/UnityMicrophoneSpeechAdapter` — lets Unity feed float microphone samples into the reusable PCM speech pipeline.

Everything else in the library remains organized by feature area and can be treated as default implementation, model contracts, or runtime internals.

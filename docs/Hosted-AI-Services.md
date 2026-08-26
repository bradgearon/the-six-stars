# Hosted AI services

The desktop app can replace the local main LLM, speech-to-text service, and streaming TTS service independently. All three hosted providers are disabled by default; the existing local configuration remains the default path.

## Fast setup

Put secrets in `src/Wds.SixStars.Ui/appsettings.Local.json` rather than committing them to `appsettings.json`:

```json
{
  "HostedServices": {
    "LLM": {
      "Enabled": true,
      "ApiKey": "YOUR_GOOGLE_AI_STUDIO_KEY"
    },
    "ASR": {
      "Enabled": true,
      "ApiKey": "YOUR_GROQ_KEY"
    },
    "TTS": {
      "Enabled": true,
      "ApiKey": "YOUR_DASHSCOPE_KEY"
    }
  }
}
```

Each switch is independent. For example, hosted LLM + Groq ASR can be enabled while continuing to use the local Qwen WebSocket TTS server.

Keys can also come from environment variables:

```text
SIX_STARS_HOSTED_LLM_ENABLED=true
GEMINI_API_KEY=...

SIX_STARS_HOSTED_ASR_ENABLED=true
GROQ_API_KEY=...

SIX_STARS_HOSTED_TTS_ENABLED=true
DASHSCOPE_API_KEY=...
```

The generic key aliases `SIX_STARS_HOSTED_LLM_API_KEY`, `SIX_STARS_HOSTED_ASR_API_KEY`, and `SIX_STARS_HOSTED_TTS_API_KEY` are also supported.

## Hosted LLM — Google AI Studio

Default model: `gemma-4-26b-a4b-it`.

When `HostedServices:LLM:Enabled=true`, the same `ILocalChatModelClient` abstraction used by the local runtime is backed by Google AI Studio. This covers normal Star replies, streaming foreground replies, background memory review, daily-state work, writing/Constellation work, and the other main Star-model operations that already go through `StructuredStarAgent`.

The adapter preserves system instructions and multi-turn user/model history, uses `thinkingLevel=high` by default, and uses Google streaming for foreground text. `EnableGoogleSearch` is optional and defaults to false. Search is only attached to ordinary text/streaming requests; JSON memory/review calls do not mix search tooling into structured output.

The existing OpenAI-shaped response schemas are translated into the JSON Schema subset accepted by Google's structured-output API. Unsupported descriptive constraints such as `maxLength` are stripped before sending. If the hosted model rejects a schema with HTTP 400, the adapter retries the same hosted request in `application/json` mode. If that response mode is also unavailable on the Gemma deployment, it retries once more as ordinary hosted `generateContent`; the review prompt still requires JSON. It never silently falls back to the local model or mock data.

Useful overrides:

```text
SIX_STARS_HOSTED_LLM_MODEL=gemma-4-26b-a4b-it
SIX_STARS_HOSTED_LLM_THINKING_LEVEL=high
SIX_STARS_HOSTED_LLM_GOOGLE_SEARCH=false
SIX_STARS_HOSTED_LLM_TIMEOUT_SECONDS=600
SIX_STARS_HOSTED_LLM_ENDPOINT_BASE_URL=https://generativelanguage.googleapis.com/v1beta
```

Existing shared LLM controls such as `SIX_STARS_LLM_TEMPERATURE`, `SIX_STARS_LLM_MAX_TOKENS`, `SIX_STARS_LLM_SPEECH_MAX_TOKENS`, `SIX_STARS_LLM_REVIEW_MAX_TOKENS`, and LLM exchange logging still apply.

The dedicated `ScreenLLM` vision path is intentionally unchanged; it remains independently configured under `ScreenLLM`.

## Hosted ASR — Groq

Default model: `whisper-large-v3-turbo`.

When `HostedServices:ASR:Enabled=true`, the existing microphone WAV pipeline posts the captured audio to Groq's OpenAI-compatible transcription endpoint. The app sends `model`, `file`, `language`, and `response_format=json` and consumes the returned `text` exactly through the existing speech-transcript path.

Useful overrides:

```text
SIX_STARS_HOSTED_ASR_MODEL=whisper-large-v3-turbo
SIX_STARS_HOSTED_ASR_LANGUAGE=en
SIX_STARS_HOSTED_ASR_TIMEOUT_SECONDS=60
SIX_STARS_HOSTED_ASR_ENDPOINT=https://api.groq.com/openai/v1/audio/transcriptions
```

## Hosted TTS — Alibaba Qwen3-TTS voice cloning

Default synthesis model: `qwen3-tts-vc-2026-01-22`.

This path uses the Qwen3-TTS voice-cloning API rather than Qwen-Audio-TTS because Qwen3-TTS can enroll the app's existing local Star clone WAV directly as a Base64 data URI. Qwen-Audio-TTS voice enrollment requires an externally accessible audio URL and a workspace-specific endpoint, which would prevent the desired "enable it and provide a key" setup.

On the first hosted TTS request for a Star, the generator resolves the existing `VoiceCloneAudioFile`, enrolls it using `qwen-voice-enrollment`, receives the hosted voice ID, and writes that ID to `state/hosted-tts-voices.json`. Later runs reuse the cached voice and do not enroll it again. A pre-created voice can also be supplied under `HostedServices:TTS:VoiceIds` keyed by Star ID.

The six bundled clone WAVs are already 16-bit, mono, 24 kHz samples in the roughly 12–16 second range, matching the published Qwen3-TTS VC format/rate/duration requirements.

The existing sentence/chunk speech pipeline remains in charge. Each text part is synthesized in order, downloaded as hosted audio, and emitted through the same `IStreamingVoiceGenerator` events used by local WebSocket TTS.

Useful overrides:

```text
SIX_STARS_HOSTED_TTS_MODEL=qwen3-tts-vc-2026-01-22
SIX_STARS_HOSTED_TTS_ENROLLMENT_MODEL=qwen-voice-enrollment
SIX_STARS_HOSTED_TTS_LANGUAGE=English
SIX_STARS_HOSTED_TTS_AUTO_CLONE=true
SIX_STARS_HOSTED_TTS_VOICE_CACHE=state/hosted-tts-voices.json
SIX_STARS_HOSTED_TTS_TIMEOUT_SECONDS=120
SIX_STARS_HOSTED_TTS_ENDPOINT=https://dashscope-intl.aliyuncs.com/api/v1/services/aigc/multimodal-generation/generation
SIX_STARS_HOSTED_TTS_ENROLLMENT_ENDPOINT=https://dashscope-intl.aliyuncs.com/api/v1/services/audio/tts/customization
```

The default endpoints are the Alibaba Model Studio Singapore-region endpoints and therefore expect a compatible Singapore-region DashScope API key.

# TTS Language Sanitizer Fix

Fixes Qwen3-TTS errors like:

```text
ValueError: Unsupported languages: [ Warm and poetic. ]
```

Cause: a local LLM can place voice performance style text in `voiceDirective.language`.
Qwen expects a real language name only.

Changes:

- Added `VoiceLanguageNormalizer` in the API voice layer.
- WebSocket and HTTP TTS payloads now sanitize language before sending to Qwen.
- Streaming and non-streaming voice pipelines normalize invalid language values to `English`.
- WebSocket Python TTS server also validates the incoming language and falls back safely.
- JSON schema now restricts `voiceDirective.language` to real supported language names.
- Added a lenient `StoryKind` JSON converter so an invalid `storyKind` no longer discards an otherwise good LLM response.

Valid language values are:

```text
Auto, English, Chinese, French, German, Spanish, Italian, Japanese, Korean, Portuguese, Russian
```

Tone/style words such as `warm`, `poetic`, `gentle`, `radiant`, or `solemn` belong in:

```text
voiceDirective.deliveryInstructions
voiceDirective.prosody
voiceDirective.pace
voiceDirective.pitch
voiceDirective.volume
```

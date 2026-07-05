# No per-request max_tokens by default for Gemma 4 / llama.cpp

This update keeps the Gemma 4 reasoning fix, but stops sending `max_tokens` by default.

## Why

The local model previously returned `finish_reason=length` with empty `message.content` because the generation budget was consumed before the final JSON reached the content channel. The runtime now treats token caps as opt-in only.

## Defaults

- `LLM.MaxTokens = 0`
- `LLM.SpeechMaxTokens = 0`
- `LLM.ReviewMaxTokens = 0`
- `ScreenLLM.MaxTokens = 0`
- launcher environment defaults also use `0`

`0` means: do not include `max_tokens` in the OpenAI-compatible request body.

## llama-server

The manifest uses `--predict -1` with reasoning disabled:

```text
--predict -1 --reasoning off --reasoning-budget 0 --reasoning-format none
```

Prompt contracts and JSON format instructions are responsible for making the model stop naturally. If a backend needs a cap, set one explicitly with environment variables such as `SIX_STARS_LLM_SPEECH_MAX_TOKENS=768`.

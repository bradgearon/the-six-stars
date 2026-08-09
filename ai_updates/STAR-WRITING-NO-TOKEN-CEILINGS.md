# Star Writing Requests: No Token Ceilings

All Star writing LLM requests now pass `0` for the optional per-request token limit, so `OpenAiCompatibleChatModelClient` omits `max_tokens` from the outgoing payload.

This applies to:

- `star-writing-word-pairs`
- `star-writing-compose`
- `star-writing-improve-pass-N`
- `star-writing-rate`
- `star-writing-rate-repair`
- `star-writing-perform`

The response shape remains bounded by the requested JSON structure and parser validation, but output is no longer silently truncated by an application-added completion-token ceiling.

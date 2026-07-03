# No-Mock LLM Salvage Update

This update fixes the case where the local LLM visibly returns a response, but the UI still shows mock data.

## What was happening

The app used a `FallbackStarAgent` around the real structured LLM agent. If the model returned non-perfect JSON, truncated JSON, snake_case JSON, or a response shape the parser did not understand, the structured parser threw an exception. `FallbackStarAgent` caught that exception and silently generated a mock Star reply.

So the LLM could be working, but the app still displayed/voiced mock data because JSON parsing failed after the HTTP request succeeded.

## What changed

- When an LLM endpoint is configured, `MainViewModel.CreateStarAgent()` now uses `StructuredStarAgent` directly instead of wrapping it in `FallbackStarAgent`.
- If no LLM endpoint is configured, the app still uses `MockStarAgent` for offline testing.
- `StructuredStarAgent` now salvages real LLM output instead of throwing on imperfect JSON:
  - parses normal camelCase JSON,
  - recognizes common aliases like `spoken_text`, `reply`, `response`, and `message`,
  - extracts `spokenText` from malformed/truncated JSON when possible,
  - uses raw plain-text LLM replies as the spoken response when the model ignores the JSON contract,
  - creates fallback `conversationSummary`, `changesSummary`, and `importantPlayerQuotes` so Star memory still updates.
- `OpenAiCompatibleChatModelClient` now retries local endpoints with response-format fallbacks:
  1. `json_schema`,
  2. `json_object`,
  3. no `response_format`.
- The OpenAI-compatible response parser now understands more local backend shapes, including `choices[0].message.content`, `choices[0].text`, direct `content`, direct `response`, and content arrays.

## Expected result

If the LLM returns anything usable, the UI should show it as a real LLM response and the Star state should update from it. You should no longer see mock data just because the model produced imperfect structured JSON.

If the LLM endpoint is completely down, the UI will now show the real error instead of hiding it behind mock dialogue.

## Debug flags

When a real LLM response is salvaged, debug output may include:

- `llm_structured_json_parse_failed_salvaged`
- `llm_raw_response_used_not_mock`
- `llm_spoken_text_salvaged`

These are not mock flags. They mean the app used real model output but had to repair/convert it into the game response format.

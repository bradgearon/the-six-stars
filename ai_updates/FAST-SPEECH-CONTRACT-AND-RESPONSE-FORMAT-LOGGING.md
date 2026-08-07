# Fast speech contract and response-format logging

## Fast foreground prompt

The `FAST SPEECH CONTRACT` section is now intentionally limited to the compact wording requested for:

- the five foreground fields,
- the three-field `informationNeeds` array,
- the nine valid information-source subjects,
- creative-story lookup behavior, and
- ordered `spokenText` TTS chunks.

The previous extra explanations, exact-shape example, automatic `needsMoreInformation` coupling text, and duplicate long-answer instructions were removed from this section.

## Request logging

Every local chat request log now shows both:

- `ResponseFormatSent`: the exact `response_format` value placed in the HTTP payload for that attempt, and
- `RequestedResponseFormatDefinition`: the complete response-format/schema object supplied by `StructuredStarAgent`, even when the current fallback mode sends only `json_object` or sends no response format.

This makes it clear whether the endpoint received the full JSON schema, generic JSON-object mode, or no response-format constraint.

## Spoken-output safety

Model control-channel text such as `<|channel>thought<channel|>{...}` is no longer treated as audible speech when the returned JSON omitted `spokenText`. The parser still reads the JSON object, but internal channel markers or a JSON-only fallback are not sent to TTS. This is response-shape cleanup only; no deterministic slime, HP, monster, spell, or other player-text routing checks were added.

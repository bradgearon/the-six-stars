# Background review: primitive-only structured output

The background memory review must stay intentionally cheap to constrain.

## Contract

The JSON response has one root object, but every value inside it is one of:

- string
- number
- boolean
- array of strings

There are **no nested response objects and no arrays of objects**.

Former object records are represented as positional strings:

- `suggestedStateDeltas`: `heartDelta^trustDelta^romanceDelta^emotionalConnectionPercentDelta`
- `rememberedMarkers[]`: `id^markerName^description^locationText^coordinates^area^sourcePlayerQuote^reminderText^confidence^isActive^commaSeparatedTags`
- `learnedFunctions[]`: `id^name^description^sourcePlayerQuote^confidence^isActive^commaSeparatedTags`
- `girlfriendNeedsMetVector[]` and `girlfriendWantsMetVector[]`: `itemId^displayName^confidence^score^reason`

The model must not put `^` inside prose fields in those records. Empty fields are represented by adjacent separators.

Desktop and Android decode these strings back into the canonical `StarState` object shapes before deterministic state/memory processing. The canonical save/import model is unchanged.

## Which schema file to use

Three examples are checked in under `ai_updates/background-review-schema/`:

- `background-review.raw-schema.json` — **paste this when a UI asks for a JSON Schema**. Its root is `{ "type": "object", ... }`.
- `background-review.openai-response-format.json` — use this only as the OpenAI-compatible API `response_format` value. Its outer `type` is `json_schema` because that outer object is a response-format wrapper, not the schema itself.
- `background-review.google-generation-config.json` — current Google GenerateContent shape. The raw schema goes under `generationConfig.responseFormat.text.schema` and the MIME type is `application/json`.

Do not paste the OpenAI wrapper into a field that asks for the raw JSON Schema. That is the source of the confusing `data/type` validator error where `json_schema` is interpreted as though it were a JSON data type.

## Request behavior

The Android protected memory review uses **non-streaming `generateContent`** again. Foreground Star speech remains streamed. The background review first tries Google's current `generationConfig.responseFormat.text` structured-output shape. A legacy Google structured-output shape is attempted only after an HTTP 400, and schema-less JSON/prompt JSON are further compatibility fallbacks.

The desktop OpenAI-compatible/local path keeps the normal `response_format={type:"json_schema",...}` API wrapper but now supplies the same primitive-only raw schema inside it.

Keep the response schema primitive-only unless there is a measured reason to change it. Nested object generation has shown materially worse latency in local testing.

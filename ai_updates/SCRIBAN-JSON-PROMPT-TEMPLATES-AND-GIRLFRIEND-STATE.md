# Scriban JSON prompt templates and compact girlfriend state

## Prompt rendering

- The API now uses the real Scriban package instead of placeholder replacement.
- Every active LLM prompt family is rendered from an `.sbn` file under `Content/PromptTemplates`.
- Each render receives one JSON-compatible C# object. `PromptTemplateRenderer` serializes that object with camel-case names, converts it into Scriban values, and exposes the single root variable `data`.
- Templates are parsed once and cached until their source file timestamp changes.
- Missing template variables are treated as errors so prompt/model drift fails visibly instead of silently leaving placeholders in a request.

Templated prompt families:

- foreground speech/chat/story
- at-rest Star initiative
- background emotion/memory review
- daily Star state
- hourly girlfriend state
- information-need selection
- interstar relay
- interstar reaction
- interstar feedback

The compatibility `BuildMessages` entry point now delegates to the same foreground template path used by `StructuredStarAgent`.

## Girlfriend needs and wants

Historical need/want vectors are no longer injected into normal foreground speech, daily-state, or background-review prompts. Those prompts receive only the current compact hourly girlfriend state when it is relevant.

The hourly girlfriend-state refresh receives the last three compact snapshots only:

- desired needs
- desired wants
- met needs
- met wants
- compact aggregate performance
- the previous current need/want vectors

Each historical snapshot is reduced to its top five items in `itemId:score:confidence` format.

Hourly generation now explicitly varies needs and wants across refreshes. A genuinely persistent need may remain high, but ordinary impulses should rotate toward fresh, personality-matched choices.

Needs and wants may describe either:

- something she wants from the Last Cat;
- something she wants or needs to say;
- something she wants or needs to do herself;
- something she wants to initiate with him; or
- something she wants them to do or experience together.

Girlfriend-mode foreground instructions tell her to sometimes perform or initiate one fitting current need/want in the response, while avoiding forced checklist behavior or repeating the same impulse every turn.

## State compatibility

No persisted state schema changed. Existing girlfriend history remains readable; only prompt selection and compaction changed.

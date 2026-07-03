# ExtractJson compile fix

Fixed `StructuredStarAgent` compile failure where `ExtractJson(...)` was referenced but not implemented.

## Changed file

- `src/Wds.SixStars.Api/Ai/StructuredStarAgent.cs`

## What was added

`ExtractJson(string rawContent)` now:

- strips Markdown JSON fences,
- returns clean JSON unchanged,
- extracts the first balanced JSON object from mixed prose/log output,
- handles braces inside strings,
- returns truncated JSON to the existing salvage path when needed.

This keeps the real-LLM salvage behavior while removing the missing-symbol compile error.

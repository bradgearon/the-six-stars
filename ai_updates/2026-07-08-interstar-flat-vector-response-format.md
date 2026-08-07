# Interstar flat vector response format

Updated interstar vector requests to avoid slow object-shaped emotion-vector outputs.

## Changed

- Interstar reaction prompts now require `lastCatEmotionalEffectVector` and `lastCatNegativeEmotionVector` as compact semicolon strings.
- Interstar reaction prompts now require flat source-Star vector aliases such as `elusiaaEmotionalEffectVector` and `elusiaaNegativeEmotionalEffectVector` instead of relying on a bulky `interstarEmotionVectors` object array.
- Interstar feedback prompts now require flat per-responding-Star vector aliases such as `kaeliaEmotionalEffectVector` and `kaeliaNegativeEmotionalEffectVector`.
- The JSON response format is now built with the specific Star aliases for each interstar request.
- The parser accepts the new flat aliases, the old source-Star aliases, and the old array shape for backward compatibility.
- If a local model accidentally returns a vector as a small object with a `vector` field, the string converter now extracts the vector text instead of failing the whole interstar parse.
- Runtime source-Star normalization now turns source-Star fields into durable `InterstarEmotionVectors` entries so relationship deltas are applied.
- Zero-vector source aliases no longer mask non-zero flat per-Star aliases.

## Vector rule

Every emotion vector is a single string in the existing compact format:

`confidence:score;confidence:score;...`

Positive vectors use the canonical Star emotion order. Negative vectors use the seven negative/boundary emotion order.

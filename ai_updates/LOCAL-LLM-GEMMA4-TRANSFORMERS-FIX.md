# Local LLM Gemma 4 Transformers Fix

Fixes the local LLM startup failure:

```text
AttributeError: 'list' object has no attribute 'keys'
```

## Cause

Gemma 4 uses the newer `gemma4` architecture and newer tokenizer configuration. Older Transformers 4.x builds can fail while loading the tokenizer before the model is available.

## Changes

- Default local LLM model changed from `google/gemma-4-e4b` to the official instruction-tuned repo id `google/gemma-4-E4B-it`.
- `requirements.txt` and `scripts/requirements.txt` now require `transformers>=5.5.0`.
- `local_llm_openai_server.py` now checks the Transformers version before loading Gemma 4 and prints a clear fix command instead of the confusing tokenizer stack trace.
- Gemma 4 loading now tries `AutoProcessor` first, matching the documented Gemma 4 Transformers path, then falls back to `AutoTokenizer`.
- Removed `TRANSFORMERS_CACHE` from the C# local model environment setup so Transformers v5 does not warn about the deprecated variable.
- Added `scripts/repair_gemma4_transformers.cmd` to upgrade the local Python deps and retry the LLM server.

## Use

From the repo root:

```bat
call .venv\scripts\activate.cmd
python -m pip install --upgrade "transformers>=5.5.0" "huggingface-hub>=0.24.0" accelerate safetensors sentencepiece protobuf packaging
python scripts\local_llm_openai_server.py
```

Or run:

```bat
scripts\repair_gemma4_transformers.cmd
```

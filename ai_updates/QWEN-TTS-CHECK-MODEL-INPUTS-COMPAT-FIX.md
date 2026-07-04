# Qwen TTS check_model_inputs compatibility fix

## Problem

The WebSocket Qwen TTS server could connect successfully but fail voice generation with:

```text
Generation error: TypeError: check_model_inputs() missing 1 required positional argument: 'func'
```

This happens when the Python venv has a Qwen3-TTS / Transformers compatibility mismatch, often after experimenting with local Gemma/Transformers versions.

## Fix

- Added a runtime compatibility shim for `transformers.utils.generic.check_model_inputs` before importing `qwen_tts` or `faster_qwen3_tts`.
- Added automatic fallback: if `FasterQwen3TTS` fails to load or generate, the server falls back to official `qwen_tts` for the rest of that server session.
- Pinned requirements back to `transformers==4.57.3` for the voice stack because LM Studio is now the default LLM path.
- Added `scripts/repair_qwen_tts_voice.cmd` to repair the venv if the previous Gemma dependency changes left it incompatible.

## Notes

DirectSound playback was not changed. The log showed the LLM response succeeded and the failure occurred inside the WebSocket TTS generation path.

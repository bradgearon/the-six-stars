# Qwen TTS meta tensor safe fallback fix

This update fixes the Android voice failure that reported:

`NotImplementedError: Cannot copy out of meta tensor; no data! Please use torch.nn.Module.to_empty() instead of torch.nn.Module.to()...`

That error is not Android audio playback and it is not .NET piping a GPU object to the speakers. It happens inside the Python Qwen/faster-qwen3/Transformers stack while the model is being moved/materialized on the GPU.

Changes:

- The official `qwen_tts` fallback now defaults to `QWEN_TTS_OFFICIAL_DEVICE_MAP=auto` on CUDA instead of forcing `cuda:0`.
- If the fast `FasterQwen3TTS` backend loads but fails during generation with a meta tensor/device-placement error, the server resets into safe official backend mode for the rest of the session.
- Safe official backend mode disables CUDA graph helper calls after a meta tensor failure so they do not re-trigger the same broken movement path.
- The model cache and voice clone prompt cache are cleared before retrying.
- The launcher and `start_services.py` now pass the safe official device-map default.

Manual override options:

```bat
set QWEN_TTS_OFFICIAL_DEVICE_MAP=auto
set QWEN_TTS_USE_FASTER_QWEN3=false
set QWEN_TTS_USE_CUDA_GRAPHS=false
```

Use the last two only if the fast backend keeps failing in your local Python environment.

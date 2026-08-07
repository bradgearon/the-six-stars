# TTS serialized generation / meta tensor root fix

This update corrects the earlier assumption that the Android socket was involved in the Qwen meta-tensor error.

The desktop app and Android phone both only receive WAV bytes from the Python WebSocket TTS service. The GPU/model object never crosses into .NET or Android. If desktop playback and Android playback both fail with `Cannot copy out of meta tensor`, the failure is inside the Python Qwen generation path.

Changes:

- Qwen generation is now protected by a single process-wide generation lock.
- Default chunk generation remains `first_then_prefetch`; parallel generation is opt-in only.
- `QWEN_TTS_PARALLEL_CHUNKS` now defaults to `false`.
- The .NET launcher now explicitly passes `QWEN_TTS_PARALLEL_CHUNKS=false`.
- Official qwen_tts fallback no longer defaults to `device_map=auto`; it defaults back to `cuda:0` on CUDA, matching the older explicit-device path.
- `device_map=auto` is only attempted when `QWEN_TTS_TRY_AUTO_DEVICE_MAP=true` is explicitly set.

Reason:

Qwen/FasterQwen CUDA graph and prompt/cache state is not safe to hit from multiple concurrent chunk/request threads. Parallel chunk generation can surface misleading meta-tensor errors or leave backend state broken. The proper shape is: HTTP/WebSocket clients can stream concurrently, but the loaded Qwen model itself should behave like one voice queue.

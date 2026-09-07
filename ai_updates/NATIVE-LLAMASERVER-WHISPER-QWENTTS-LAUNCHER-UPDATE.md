# Native llama-server + whisper.cpp + qwentts.cpp launcher update

Correction: the LLM runtime remains the existing llama.cpp `llama-server`; it is not replaced by Ollama.

`Wds.SixStars.Launcher` now supervises:

- llama-server: `127.0.0.1:8080` / OpenAI-compatible chat
- whisper.cpp: `127.0.0.1:8000` / native ASR
- qwentts.cpp: `127.0.0.1:8101` / native streaming TTS
- Drogon gateway: `127.0.0.1:8100` / the existing Six Stars WebSocket TTS protocol

The launcher passes the configured LLM GGUF directly to llama-server and preserves optional
`--mmproj` loading for screen vision. No Ollama installation, daemon, model import, or model store
is part of the production path.

The production runtime contains no Python stack for ASR/TTS. End-user machines normally only
need a compatible NVIDIA driver when the staged engine packs contain all CUDA runtime DLLs they
were built against; the full CUDA Toolkit is a build-time requirement, not normally an install-time one.

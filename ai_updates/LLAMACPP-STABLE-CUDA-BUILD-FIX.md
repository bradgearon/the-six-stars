# llama.cpp Stable CUDA Build Fix

The original redistributable build tried to build CUDA, Vulkan, dynamic backend loading, and every CPU backend variant at the same time. On some Windows systems this caused multiple toolchain crashes:

- MSVC `CL.exe` access violations while compiling several CPU variants.
- Vulkan `glslc.exe` shader compiler access violations.
- CUDA 13.x `ptxas` / `cicc` crashes while compiling extra CUDA architectures such as `120a` and `121a`.

The build script now defaults to a simpler stable build:

- CUDA enabled only for the NVIDIA profile.
- Vulkan disabled by default.
- Dynamic backend loading disabled by default.
- CPU all-variants disabled by default.
- `CMAKE_CUDA_ARCHITECTURES` is detected from `nvidia-smi`, typically `86` for RTX 30xx.
- CMake builds only the `llama-server` target.
- Parallel build jobs are limited to 2.

The engine output folder name remains `engines/llm/llama-cpp-dynamic-win-x64` so the existing launcher and manifest do not need to change.

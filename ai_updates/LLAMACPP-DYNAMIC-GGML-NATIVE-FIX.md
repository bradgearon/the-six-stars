# llama.cpp dynamic backend GGML_NATIVE fix

If CMake fails with:

```text
GGML_NATIVE is not compatible with GGML_BACKEND_DL, consider using GGML_CPU_ALL_VARIANTS
```

this is not a CMake version problem. The dynamic backend build must disable native CPU specialization and enable CPU variant builds.

`scripts/Build-LlamaCppDynamic.ps1` now configures llama.cpp with:

```powershell
-DGGML_BACKEND_DL=ON
-DGGML_CPU_ALL_VARIANTS=ON
-DGGML_NATIVE=OFF
-DGGML_CURL=ON
-DGGML_CCACHE=OFF
```

`LLAMA_CURL` was replaced with `GGML_CURL` because current llama.cpp warns that `LLAMA_CURL` is deprecated and ignored.

If you already have a half-configured build folder, delete it or run the script with `-Clean`:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Build-LlamaCppDynamic.ps1 -Clean -WithCuda:$true -WithVulkan:$true
```

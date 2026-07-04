# CUDA Torch Voice Setup Update

This update fixes the common Windows setup problem where `pip install -r requirements.txt` installs a CPU-only PyTorch build, causing Qwen voice generation to fail with:

```text
AssertionError: Torch not compiled with CUDA enabled
```

## Changes

- Removed `torch` from `requirements.txt` and `scripts/requirements.txt`.
- Removed direct `qwen-tts` and `faster-qwen3-tts` installs from the base requirements so they do not pull CPU-only Torch as transitive dependencies.
- Added `requirements-torch-cuda-cu126.txt` for the separate CUDA PyTorch wheel install.
- Added `setup.cmd` for one-time setup:
  - creates `.venv`
  - installs base requirements
  - installs CUDA PyTorch
  - installs Qwen TTS packages after CUDA PyTorch
  - verifies `torch.cuda.is_available()`
- Added `scripts/install_cuda_torch.cmd` to repair or reinstall the CUDA voice stack without recreating the venv.
- Added `scripts/verify_torch_cuda.py` for a quick CUDA Torch check.
- Updated `launch.cmd` to warn clearly if CUDA PyTorch is not ready.
- Updated `scripts/start_services.py` so it no longer blindly forces CUDA when the current PyTorch install is CPU-only.

## Recommended setup

Run once from the repo root:

```bat
setup.cmd
```

Then start LM Studio, load the model, and run:

```bat
launch.cmd
```

## Repair existing venv

If `.venv` already exists but Torch is CPU-only or broken:

```bat
scripts\install_cuda_torch.cmd
```

To try CUDA 12.8 wheels instead of the default CUDA 12.6 wheels:

```bat
set TORCH_CUDA_INDEX=cu128
scripts\install_cuda_torch.cmd
```

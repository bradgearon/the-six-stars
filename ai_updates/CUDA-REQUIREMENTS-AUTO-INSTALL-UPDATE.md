# CUDA requirements auto-install update

Updated `requirements.txt` and `scripts/requirements.txt` so a normal:

```bat
python -m pip install -r requirements.txt
```

installs CUDA PyTorch from the PyTorch CUDA 12.6 wheel index instead of accidentally pulling CPU-only Torch from PyPI.

## Key changes

- Added `--extra-index-url https://download.pytorch.org/whl/cu126`.
- Added explicit CUDA pins:
  - `torch==2.7.0+cu126`
  - `torchvision==0.22.0+cu126`
  - `torchaudio==2.7.0+cu126`
- Added `qwen-tts` and `faster-qwen3-tts` after the CUDA Torch pins.
- Updated `setup.cmd` so it verifies CUDA after requirements install and falls back to `scripts\install_cuda_torch.cmd` if needed.
- Updated `requirements-torch-cuda-cu126.txt` to use the same explicit pins.

## Notes

If a machine needs CUDA 12.8 instead, keep using:

```bat
set TORCH_CUDA_INDEX=cu128
scripts\install_cuda_torch.cmd
```

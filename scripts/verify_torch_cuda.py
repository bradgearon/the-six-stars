#!/usr/bin/env python3
"""Verify whether the active Python environment has CUDA-enabled PyTorch."""

from __future__ import annotations

import argparse
import sys


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--quiet", action="store_true", help="Only print errors/warnings.")
    args = parser.parse_args()

    try:
        import torch  # type: ignore
    except Exception as exc:
        if not args.quiet:
            print(f"PyTorch import failed: {type(exc).__name__}: {exc}")
        else:
            print("PyTorch import failed.")
        return 2

    version = getattr(torch, "__version__", "unknown")
    cuda_version = getattr(getattr(torch, "version", None), "cuda", None)
    cuda_available = bool(getattr(torch, "cuda", None) is not None and torch.cuda.is_available())
    device_count = int(torch.cuda.device_count()) if getattr(torch, "cuda", None) is not None else 0

    if not args.quiet:
        print(f"torch: {version}")
        print(f"torch.version.cuda: {cuda_version}")
        print(f"torch.cuda.is_available(): {cuda_available}")
        print(f"torch.cuda.device_count(): {device_count}")
        if cuda_available and device_count > 0:
            try:
                print(f"cuda device 0: {torch.cuda.get_device_name(0)}")
            except Exception:
                pass

    if not cuda_available:
        if args.quiet:
            print(f"PyTorch CUDA is not available. torch={version}, torch.version.cuda={cuda_version}")
        return 1

    return 0


if __name__ == "__main__":
    raise SystemExit(main())

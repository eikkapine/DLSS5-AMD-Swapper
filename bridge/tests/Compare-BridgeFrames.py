"""Compare matched frozen-source bridge captures, without resizing images."""
from __future__ import annotations

import argparse
import json
from pathlib import Path

import numpy as np
from PIL import Image


def read_final(directory: Path, kind: str) -> np.ndarray:
    files = list(directory.glob(f"final*_{kind}.ppm"))
    if len(files) != 1:
        raise ValueError(f"Expected one final {kind} capture in {directory}, found {len(files)}")
    with Image.open(files[0]) as image:
        return np.array(image.convert("RGB"), dtype=np.int16)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline", type=Path, required=True)
    parser.add_argument("--candidate", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--preview", type=Path)
    args = parser.parse_args()
    result: dict = {}
    arrays: dict[str, np.ndarray] = {}
    for kind in ("input", "original", "nr", "display"):
        old = read_final(args.baseline, kind)
        new = read_final(args.candidate, kind)
        if old.shape != new.shape:
            raise ValueError(f"{kind} dimensions differ: {old.shape} vs {new.shape}")
        difference = np.abs(old - new)
        result[kind] = {
            "shape": list(new.shape),
            "max_delta": int(difference.max()),
            "mean_delta": float(difference.mean()),
            "changed_channels": int(np.count_nonzero(difference)),
        }
        arrays[kind] = new
    effect = np.abs(arrays["nr"] - arrays["original"])
    result["neural_effect"] = {
        "max_delta": int(effect.max()),
        "mean_delta": float(effect.mean()),
        "nonblack": bool(np.any(arrays["nr"])),
    }
    result["pixels_identical"] = all(result[kind]["max_delta"] == 0 for kind in arrays)
    result["passed"] = result["pixels_identical"] and bool(effect.max()) and result["neural_effect"]["nonblack"]
    args.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    if args.preview:
        Image.fromarray(arrays["display"].astype(np.uint8)).save(args.preview)
    print(json.dumps(result, indent=2))
    return 0 if result["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())

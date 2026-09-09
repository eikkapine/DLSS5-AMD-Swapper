#!/usr/bin/env python3
"""Compare a bridge capture against a supplied DLSS OFF/ON reference pair.

The tool never copies the reference images. It writes metrics and registration
confidence only. Unaligned metrics are always reported. Aligned metrics are
emitted only when feature registration passes conservative confidence checks.
"""
from __future__ import annotations

import argparse
import json
import math
from pathlib import Path
from typing import Any

import cv2
import numpy as np
from PIL import Image


def load_rgb(path: Path) -> np.ndarray:
    with Image.open(path) as image:
        return np.asarray(image.convert("RGB"), dtype=np.float32)


def load_bgr(path: Path) -> np.ndarray:
    rgb = load_rgb(path).astype(np.uint8)
    return cv2.cvtColor(rgb, cv2.COLOR_RGB2BGR)


def resize_rgb(image: np.ndarray, width: int, height: int) -> np.ndarray:
    clipped = np.clip(image, 0, 255).astype(np.uint8)
    resized = cv2.resize(clipped, (width, height), interpolation=cv2.INTER_LANCZOS4)
    return resized.astype(np.float32)


def metrics(a: np.ndarray, b: np.ndarray, mask: np.ndarray | None = None) -> dict[str, Any]:
    if a.shape != b.shape:
        raise ValueError(f"Image dimensions differ: {a.shape} vs {b.shape}")
    if mask is None:
        x = a.reshape(-1, 3)
        y = b.reshape(-1, 3)
        valid_pixels = a.shape[0] * a.shape[1]
    else:
        if mask.shape != a.shape[:2]:
            raise ValueError(f"Mask dimensions differ: {mask.shape} vs {a.shape[:2]}")
        x = a[mask]
        y = b[mask]
        valid_pixels = int(mask.sum())
    if valid_pixels == 0:
        raise ValueError("No valid pixels remain for comparison")

    delta = x - y
    mse = float(np.mean(delta * delta))
    mae = float(np.mean(np.abs(delta)))
    psnr = None if mse == 0 else float(20 * math.log10(255.0 / math.sqrt(mse)))

    luma_x = x[:, 0] * 0.2126 + x[:, 1] * 0.7152 + x[:, 2] * 0.0722
    luma_y = y[:, 0] * 0.2126 + y[:, 1] * 0.7152 + y[:, 2] * 0.0722
    centered_x = luma_x - luma_x.mean()
    centered_y = luma_y - luma_y.mean()
    denominator = float(np.sqrt(np.sum(centered_x * centered_x) * np.sum(centered_y * centered_y)))
    correlation = float(np.sum(centered_x * centered_y) / denominator) if denominator else None
    return {
        "mae_rgb": mae,
        "rmse_rgb": float(math.sqrt(mse)),
        "psnr_db": psnr,
        "luma_correlation": correlation,
        "valid_pixels": valid_pixels,
    }


def delta_metrics(reference_off: np.ndarray, reference_on: np.ndarray, candidate: np.ndarray,
                  mask: np.ndarray | None = None) -> dict[str, float | None]:
    if mask is None:
        ref = (reference_on - reference_off).reshape(-1, 3)
        delta = (candidate - reference_off).reshape(-1, 3)
    else:
        ref = (reference_on - reference_off)[mask]
        delta = (candidate - reference_off)[mask]
    dot = float(np.sum(ref * delta))
    ref_norm = float(np.sum(ref * ref))
    candidate_norm = float(np.sum(delta * delta))
    return {
        "projection": dot / ref_norm if ref_norm else None,
        "cosine": dot / math.sqrt(ref_norm * candidate_norm) if ref_norm and candidate_norm else None,
        "energy_ratio": candidate_norm / ref_norm if ref_norm else None,
    }


def final_capture(directory: Path, kind: str) -> Path:
    files = sorted(directory.glob(f"final*_{kind}.ppm"))
    if len(files) != 1:
        raise ValueError(f"Expected one final {kind} capture in {directory}, found {len(files)}")
    return files[0]


def register_on_to_off(reference_on: Path, reference_off: Path) -> tuple[np.ndarray, np.ndarray, dict[str, Any]]:
    fixed = load_bgr(reference_off)
    moving = load_bgr(reference_on)
    if moving.shape[:2] != fixed.shape[:2]:
        moving = cv2.resize(moving, (fixed.shape[1], fixed.shape[0]), interpolation=cv2.INTER_LANCZOS4)

    gray_fixed = cv2.cvtColor(fixed, cv2.COLOR_BGR2GRAY)
    gray_moving = cv2.cvtColor(moving, cv2.COLOR_BGR2GRAY)
    sift = cv2.SIFT_create(nfeatures=4000)
    fixed_keys, fixed_desc = sift.detectAndCompute(gray_fixed, None)
    moving_keys, moving_desc = sift.detectAndCompute(gray_moving, None)
    if fixed_desc is None or moving_desc is None:
        return moving, np.ones(fixed.shape[:2], dtype=bool), {
            "accepted": False,
            "reason": "not_enough_features",
            "matches": 0,
            "inliers": 0,
            "inlier_ratio": 0.0,
        }

    matcher = cv2.BFMatcher(cv2.NORM_L2)
    pairs = matcher.knnMatch(moving_desc, fixed_desc, k=2)
    good = [m for m, n in pairs if m.distance < 0.72 * n.distance]
    if len(good) < 8:
        return moving, np.ones(fixed.shape[:2], dtype=bool), {
            "accepted": False,
            "reason": "not_enough_matches",
            "matches": len(good),
            "inliers": 0,
            "inlier_ratio": 0.0,
        }

    source = np.float32([moving_keys[m.queryIdx].pt for m in good]).reshape(-1, 1, 2)
    target = np.float32([fixed_keys[m.trainIdx].pt for m in good]).reshape(-1, 1, 2)
    homography, inlier_mask = cv2.findHomography(source, target, cv2.RANSAC, 3.0)
    if homography is None or inlier_mask is None or not np.isfinite(homography).all():
        return moving, np.ones(fixed.shape[:2], dtype=bool), {
            "accepted": False,
            "reason": "homography_failed",
            "matches": len(good),
            "inliers": 0,
            "inlier_ratio": 0.0,
        }

    inliers = int(inlier_mask.sum())
    ratio = float(inlier_mask.mean())
    warped = cv2.warpPerspective(moving, homography, (fixed.shape[1], fixed.shape[0]), flags=cv2.INTER_LANCZOS4)
    valid = cv2.warpPerspective(
        np.full(moving.shape[:2], 255, dtype=np.uint8),
        homography,
        (fixed.shape[1], fixed.shape[0]),
        flags=cv2.INTER_NEAREST,
    ) > 250
    valid_ratio = float(valid.mean())

    linear = homography[:2, :2]
    determinant = float(np.linalg.det(linear))
    singular_values = np.linalg.svd(linear, compute_uv=False)
    min_scale = float(singular_values.min())
    max_scale = float(singular_values.max())
    perspective = float(max(abs(homography[2, 0]), abs(homography[2, 1])))

    reasons: list[str] = []
    if len(good) < 20:
        reasons.append("too_few_matches")
    if inliers < 12:
        reasons.append("too_few_inliers")
    if ratio < 0.5:
        reasons.append("low_inlier_ratio")
    if valid_ratio < 0.5:
        reasons.append("low_valid_coverage")
    if determinant <= 0:
        reasons.append("orientation_flip_or_degenerate")
    if min_scale < 0.5 or max_scale > 2.0:
        reasons.append("implausible_scale")
    if perspective > 0.002:
        reasons.append("excessive_perspective")

    return warped, valid, {
        "accepted": not reasons,
        "reason": "accepted" if not reasons else ",".join(reasons),
        "matches": len(good),
        "inliers": inliers,
        "inlier_ratio": ratio,
        "valid_ratio": valid_ratio,
        "linear_determinant": determinant,
        "min_scale": min_scale,
        "max_scale": max_scale,
        "perspective": perspective,
        "homography": homography.tolist(),
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--off", type=Path, required=True, help="Reference image with DLSS 5 off")
    parser.add_argument("--on", type=Path, required=True, help="Reference image with DLSS 5 on")
    parser.add_argument("--bridge", type=Path, required=True, help="Bridge capture directory")
    parser.add_argument("--output", type=Path, required=True, help="JSON metrics output")
    args = parser.parse_args()

    off = load_rgb(args.off)
    on = load_rgb(args.on)
    if on.shape[:2] != off.shape[:2]:
        on = resize_rgb(on, off.shape[1], off.shape[0])

    original = load_rgb(final_capture(args.bridge, "original"))
    neural = load_rgb(final_capture(args.bridge, "nr"))
    display = load_rgb(final_capture(args.bridge, "display"))
    if display.shape[:2] != off.shape[:2]:
        raise ValueError(f"Bridge display dimensions {display.shape[:2]} do not match OFF reference {off.shape[:2]}")
    original_native = resize_rgb(original, off.shape[1], off.shape[0])
    neural_native = resize_rgb(neural, off.shape[1], off.shape[0])

    result: dict[str, Any] = {
        "schema_version": 1,
        "sizes": {
            "reference": [off.shape[1], off.shape[0]],
            "neural": [neural.shape[1], neural.shape[0]],
        },
        "unaligned": {
            "reference_off_to_on": metrics(off, on),
            "bridge_display_to_reference_on": metrics(display, on),
            "bridge_raw_nr_to_reference_on": metrics(neural_native, on),
            "bridge_original_to_reference_on": metrics(original_native, on),
            "bridge_display_change_from_off": metrics(display, off),
            "bridge_raw_nr_change_from_input": metrics(neural_native, original_native),
            "display_delta": delta_metrics(off, on, display),
            "raw_nr_delta": delta_metrics(off, on, neural_native),
        },
    }

    aligned_bgr, valid, registration = register_on_to_off(args.on, args.off)
    result["registration"] = registration
    if registration["accepted"]:
        aligned_on = cv2.cvtColor(aligned_bgr, cv2.COLOR_BGR2RGB).astype(np.float32)
        result["aligned"] = {
            "reference_off_to_on": metrics(off, aligned_on, valid),
            "bridge_display_to_reference_on": metrics(display, aligned_on, valid),
            "bridge_raw_nr_to_reference_on": metrics(neural_native, aligned_on, valid),
            "display_delta": delta_metrics(off, aligned_on, display, valid),
            "raw_nr_delta": delta_metrics(off, aligned_on, neural_native, valid),
        }
    else:
        result["aligned"] = None

    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(result, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

#!/usr/bin/env python3
"""Summarize recorded runtime logs without loading a runtime or starting capture.

Uses Python's standard library. The JSON contains numeric aggregates and source
hashes, not raw log text, local paths, window titles, process IDs or adapter IDs.
Output is create-only so an earlier measurement cannot be silently replaced.
"""

from __future__ import annotations

import argparse
from collections import defaultdict
import csv
import datetime as dt
import hashlib
import io
import json
import math
from pathlib import Path
import re
import statistics
import sys
from typing import Any

MAX_LOG_BYTES = 8 * 1024 * 1024
APIS = ("launch", "device_wait", "stream_wait", "event_wait", "copy", "copy_async")
SCHEMA_VERSION = 2
ANALYZER_VERSION = "2.0"


def read_log(path: Path) -> tuple[str, str]:
    with path.open("rb") as stream:
        raw = stream.read(MAX_LOG_BYTES + 1)
    if len(raw) > MAX_LOG_BYTES:
        raise ValueError("A selected log exceeds the 8 MiB limit")
    return raw.decode("utf-8", errors="replace"), hashlib.sha256(raw).hexdigest()


def numeric_rows(text: str) -> list[dict[str, float | int]]:
    rows: list[dict[str, float | int]] = []
    for line in text.splitlines():
        if not line.startswith("elapsed_s="):
            continue
        row: dict[str, float | int] = {}
        for key, value in re.findall(r"(\w+)=(\S+)", line):
            # Only numeric measurements are exported. Future text fields are ignored.
            if not re.fullmatch(r"-?\d+(?:\.\d+)?", value):
                continue
            number = float(value) if "." in value else int(value)
            if not math.isfinite(number):
                raise ValueError("Non-finite log measurement")
            row[key] = number
        if "elapsed_s" not in row or row["elapsed_s"] < 0:
            raise ValueError("Invalid elapsed time")
        rows.append(row)
    return rows


def contained(end: float, length: float, since: float, until: float | None) -> bool:
    # Endpoints are rounded in the logs; tolerate at most 2 ms of rounding.
    return end - length >= since - 0.002 and (until is None or end <= until + 0.002)


def summarize_cadence(text: str, since: float, until: float | None) -> dict[str, Any]:
    rows = numeric_rows(text)
    if not rows:
        raise ValueError("Cadence log contains no measurements")
    selected = []
    previous_elapsed = 0.0
    previous_changed = 0
    changed = 0
    for row in rows:
        end = float(row["elapsed_s"])
        length = float(row["interval_s"])
        count = int(row["total_changed"])
        if end <= previous_elapsed or length <= 0 or count < previous_changed:
            raise ValueError("Cadence counters are not monotonic; select one run")
        if contained(end, length, since, until):
            selected.append(row)
            changed += count - previous_changed
        previous_elapsed, previous_changed = end, count
    if not selected:
        raise ValueError("No complete cadence intervals fit the requested window")
    seconds = sum(float(row["interval_s"]) for row in selected)
    gpu_counts = all("gpu_transport_frames" in row for row in selected)
    feed_count = sum(int(row["gpu_transport_frames"]) for row in selected) if gpu_counts else None
    feed_rate = feed_count / seconds if feed_count is not None else sum(
        float(row["feed_fps"]) * float(row["interval_s"]) for row in selected
    ) / seconds
    head = text.split("elapsed_s=", 1)[0]
    modes = {}
    for key in ("width", "height", "suppress_identical_rgb", "transport", "completion_pacing"):
        match = re.search(rf"\b{key}=([A-Za-z0-9_.]+)", head)
        if match:
            modes[key] = int(match[1]) if match[1].isdigit() else match[1]
    final = rows[-1]
    result = {
        "configuration_from_header": modes,
        "effect_state_recorded": all("effect_enabled" in row and "strength" in row for row in rows),
        "shutdown_row_present": final.get("final") == 1,
        "whole_recording": {
            "elapsed_s": final["elapsed_s"],
            "changed_rgb_submissions": final["total_changed"],
            "changed_rgb_per_s": int(final["total_changed"]) / float(final["elapsed_s"]),
            "identical_submissions_suppressed": final.get("total_skipped"),
            "image_checks": final.get("total_direct_image_checks"),
            "keepalive_feeds": final.get("keepalive_feeds"),
            "publication_rechecks": final.get("publication_rechecks"),
        },
        "selected_window": {
            "requested_since_s": since,
            "requested_until_s": until,
            "actual_start_s": float(selected[0]["elapsed_s"]) - float(selected[0]["interval_s"]),
            "actual_end_s": selected[-1]["elapsed_s"],
            "summed_interval_s": seconds,
            "changed_rgb_submissions": changed,
            "changed_rgb_per_s": changed / seconds,
            "feed_iterations": feed_count,
            "feed_iterations_per_s": feed_rate,
            "capture_per_s_from_rounded_rates": sum(
                float(row["capture_fps"]) * float(row["interval_s"]) for row in selected
            ) / seconds,
            "occluded_submissions": sum(int(row.get("occluded_submissions", 0)) for row in selected),
        },
    }
    return result


def summarize_hip(text: str, since: float, until: float | None) -> dict[str, Any]:
    groups: dict[int, list[dict[str, float | int]]] = defaultdict(list)
    for row in numeric_rows(text):
        length = float(row["window_ms"]) / 1000.0
        if length <= 0:
            raise ValueError("Invalid HIP observation window")
        if contained(float(row["elapsed_s"]), length, since, until):
            groups[int(row["thread"])].append(row)
    threads = []
    for rows in groups.values():
        totals = {}
        for api in APIS:
            calls = sum(int(row.get(api + "_calls", 0)) for row in rows)
            milliseconds = sum(float(row.get(api + "_ms", 0)) for row in rows)
            totals[api] = {
                "calls": calls,
                "wall_ms": milliseconds,
                "mean_wall_ms_per_call": milliseconds / calls if calls else None,
                "maximum_call_ms": max(float(row.get(api + "_max_ms", 0)) for row in rows),
                "errors": sum(int(row.get(api + "_errors", 0)) for row in rows),
            }
        waits = totals["device_wait"]["calls"]
        threads.append({
            "label": f"observed-thread-{len(threads) + 1}",
            "summed_window_ms": sum(float(row["window_ms"]) for row in rows),
            "nondefault_launches": sum(int(row.get("nondefault_launches", 0)) for row in rows),
            "launch_calls_per_device_wait": totals["launch"]["calls"] / waits if waits else None,
            "launch_wall_ms_per_device_wait": totals["launch"]["wall_ms"] / waits if waits else None,
            "apis": totals,
        })
    return {
        "requested_since_s": since,
        "requested_until_s": until,
        "clock_origin": "HIP observer start; distinct from visible cadence start",
        "threads": threads,
    }


def _float_field(row: dict[str, str], *names: str) -> float | None:
    lower = {key.lower(): value for key, value in row.items() if key}
    for name in names:
        raw = lower.get(name.lower())
        if raw is None or raw.strip().upper() in {"", "NA", "N/A"}:
            continue
        try:
            value = float(raw)
        except ValueError:
            continue
        if math.isfinite(value):
            return value
    return None


def _percentile(values: list[float], fraction: float) -> float | None:
    if not values:
        return None
    ordered = sorted(values)
    if len(ordered) == 1:
        return ordered[0]
    position = fraction * (len(ordered) - 1)
    low = int(math.floor(position))
    high = int(math.ceil(position))
    if low == high:
        return ordered[low]
    weight = position - low
    return ordered[low] * (1.0 - weight) + ordered[high] * weight


def summarize_presentmon(text: str) -> dict[str, Any]:
    """Summarize one PresentMon CSV without exposing local identifiers.

    PresentMon can record more than one swap chain for a process. The primary
    chain is selected mechanically as the chain with the most valid present
    intervals. All rates below are derived only from the CSV rows.
    """

    reader = csv.DictReader(io.StringIO(text))
    if not reader.fieldnames:
        raise ValueError("PresentMon CSV has no header")

    groups: dict[tuple[str, str, str], list[dict[str, str]]] = defaultdict(list)
    total_rows = 0
    for row in reader:
        total_rows += 1
        key = (
            row.get("Application", ""),
            row.get("ProcessID", ""),
            row.get("SwapChainAddress", ""),
        )
        groups[key].append(row)
    if not groups:
        raise ValueError("PresentMon CSV has no frame rows")

    def present_intervals(rows: list[dict[str, str]]) -> list[float]:
        values = []
        for row in rows:
            value = _float_field(row, "MsBetweenPresents", "msBetweenPresents", "FrameTime")
            if value is not None and value > 0:
                values.append(value)
        return values

    primary_key, primary_rows = max(groups.items(), key=lambda item: len(present_intervals(item[1])))
    presents = present_intervals(primary_rows)
    if not presents:
        raise ValueError("PresentMon CSV has no valid present/frame intervals")

    displayed = []
    gpu_time = []
    for row in primary_rows:
        value = _float_field(row, "MsBetweenDisplayChange", "msBetweenDisplayChange", "DisplayedTime")
        if value is not None and value > 0:
            displayed.append(value)
        value = _float_field(row, "GPUTime", "MsGPUTime", "msGPUTime", "msGPUActive")
        if value is not None and value >= 0:
            gpu_time.append(value)

    present_mean = statistics.fmean(presents)
    display_mean = statistics.fmean(displayed) if displayed else None
    result: dict[str, Any] = {
        "source_rows": total_rows,
        "primary_swapchain_rows": len(primary_rows),
        "application": Path(primary_key[0]).name if primary_key[0] else None,
        "present_intervals": len(presents),
        "present_interval_ms": {
            "mean": present_mean,
            "median": statistics.median(presents),
            "p95": _percentile(presents, 0.95),
            "p99": _percentile(presents, 0.99),
        },
        "present_rate_fps": 1000.0 / present_mean,
        "display_intervals": len(displayed),
        "display_interval_ms": {
            "mean": display_mean,
            "median": statistics.median(displayed) if displayed else None,
            "p95": _percentile(displayed, 0.95),
        },
        "display_rate_fps": 1000.0 / display_mean if display_mean else None,
    }
    if gpu_time:
        result["gpu_time_ms"] = {
            "mean": statistics.fmean(gpu_time),
            "median": statistics.median(gpu_time),
            "p95": _percentile(gpu_time, 0.95),
        }
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cadence", type=Path, help="Lossless Scaling bridge cadence log")
    parser.add_argument("--hip", type=Path)
    parser.add_argument("--runtime-log", type=Path)
    parser.add_argument("--presentmon", type=Path, help="PresentMon CSV captured for the target game/process")
    parser.add_argument("--run-id", required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--bridge-sha256", help="Optional bridge hash verified against this run's deployment record")
    parser.add_argument("--since", type=float, default=20.0)
    parser.add_argument("--until", type=float)
    parser.add_argument("--hip-since", type=float, default=20.0)
    parser.add_argument("--hip-until", type=float)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    try:
        if not any((args.cadence, args.hip, args.runtime_log, args.presentmon)):
            raise ValueError("Select at least one source log")
        for value in (args.run_id, args.version):
            if not re.fullmatch(r"[A-Za-z0-9_.-]{1,80}", value):
                raise ValueError("Run ID and version must be short alphanumeric labels")
        if args.bridge_sha256 is not None and not re.fullmatch(r"[a-fA-F0-9]{64}", args.bridge_sha256):
            raise ValueError("Bridge SHA256 must contain 64 hexadecimal characters")
        for start, end in ((args.since, args.until), (args.hip_since, args.hip_until)):
            if not math.isfinite(start) or start < 0 or (end is not None and (not math.isfinite(end) or end <= start)):
                raise ValueError("Invalid measurement window")
        report: dict[str, Any] = {
            "schema_version": SCHEMA_VERSION,
            "analyzer_version": ANALYZER_VERSION,
            "run_id": args.run_id,
            "version": args.version,
            "analysis_created_at_utc": dt.datetime.now(dt.timezone.utc).isoformat(),
            "source_sha256": {},
            "limitations": [
                "Analysis reads existing logs only; no tests, playback or GPU work are started.",
                "Changed RGB submissions, HIP calls, neural jobs and PresentMon frame rates are distinct.",
                "Game/display FPS is published only when a PresentMon CSV is supplied.",
                "Window selection includes complete logged intervals only; endpoints are rounded.",
                "Whole-run averages can include bypass, strength changes and startup; effect state may be missing.",
                "Different gameplay runs are observational comparisons, not controlled A/B benchmarks.",
                "HIP device-wait duration includes queued work and contention, not isolated kernel time.",
                "HIP threads have overlapping windows and must not be summed as one critical path.",
            ],
        }
        if args.bridge_sha256:
            report["bridge_sha256"] = args.bridge_sha256.lower()
            report["hash_association"] = "Provided by caller; verify with the deployment record, not the current executable alone"
        if args.cadence:
            cadence, digest = read_log(args.cadence)
            report["source_sha256"]["cadence"] = digest
            report["cadence"] = summarize_cadence(cadence, args.since, args.until)
        if args.hip:
            text, digest = read_log(args.hip)
            report["source_sha256"]["hip"] = digest
            report["hip"] = summarize_hip(text, args.hip_since, args.hip_until)
        if args.runtime_log:
            text, digest = read_log(args.runtime_log)
            report["source_sha256"]["runtime"] = digest
            version = re.search(r"dlssnr_amd (v[\d.]+) \(build ([a-fA-F0-9]+)\)", text)
            sample_rows = re.findall(
                r"network job (\d+) done in (\d+) ms \(([\d.]+) ms network on the GPU, "
                r"([\d.]+) ms waiting for the capture; history (on|off), (zero-copy|copied)\)",
                text,
                flags=re.IGNORECASE,
            )
            samples = [
                {
                    "job": int(job),
                    "wall_ms": int(wall),
                    "network_gpu_ms": float(gpu),
                    "capture_wait_ms": float(wait),
                    "history": history.lower() == "on",
                    "zero_copy": zero_copy.lower() == "zero-copy",
                }
                for job, wall, gpu, wait, history, zero_copy in sample_rows
            ]
            staging = re.search(
                r"staging ready: colour (\d+)x(\d+) dxgi \d+ .*?; motion (\d+)x(\d+) dxgi \d+; "
                r"depth (\d+)x(\d+) dxgi \d+ \(inverted (\d+)\); exposure (yes|no); residual (on|off)",
                text,
                flags=re.IGNORECASE,
            )
            interop = re.search(
                r"interop: inputs shared \((zero-copy|copied)\), output shared \((zero-copy|copied)\); mode ([^\r\n]+)",
                text,
                flags=re.IGNORECASE,
            )
            hip = re.search(r"env: HIP: (\d+) device\(s\), driver (\d+), runtime (\d+);", text)
            swapchain = re.search(r"env: swapchain (\d+)x(\d+) format \d+,", text)
            report["runtime"] = {
                "version": version[1] if version else None,
                "build": version[2] if version else None,
                "fidelityfx_dispatch_detected": bool(re.search(r"(?im)^first ffxDispatch type ", text)),
                "fidelityfx_upscaler_hooks": len(re.findall(r"(?im)^hooked amd_fidelityfx_.*!ffxDispatch", text)),
                "sampled_jobs": samples,
                "observed_fault_lines": len(re.findall(r"(?im)^FAULT:", text)),
                "observed_gpu_error_lines": len(re.findall(r"(?im)^job \d+ GPU errors:", text)),
                "note": "Sparse job samples; marker counts are not a comprehensive runtime-health certification",
            }
            if staging:
                report["runtime"]["fsr_inputs"] = {
                    "color_size": [int(staging[1]), int(staging[2])],
                    "motion_size": [int(staging[3]), int(staging[4])],
                    "depth_size": [int(staging[5]), int(staging[6])],
                    "depth_inverted": staging[7] == "1",
                    "exposure_texture": staging[8].lower() == "yes",
                    "residual_enabled": staging[9].lower() == "on",
                }
                if swapchain:
                    output_w, output_h = int(swapchain[1]), int(swapchain[2])
                    input_w, input_h = int(staging[1]), int(staging[2])
                    report["runtime"]["processing_resolution"] = {
                        "swapchain_size": [output_w, output_h],
                        "fsr_input_size": [input_w, input_h],
                        "input_to_output_width_ratio": input_w / output_w if output_w else None,
                        "input_to_output_height_ratio": input_h / output_h if output_h else None,
                        "full_output_resolution_input": input_w == output_w and input_h == output_h,
                    }
            if interop:
                report["runtime"]["interop"] = {
                    "inputs": interop[1].lower(),
                    "output": interop[2].lower(),
                    "mode": interop[3].strip(),
                }
            if hip:
                report["runtime"]["hip_runtime"] = {
                    "device_count": int(hip[1]),
                    "driver": hip[2],
                    "runtime": hip[3],
                }
        if args.presentmon:
            text, digest = read_log(args.presentmon)
            report["source_sha256"]["presentmon"] = digest
            report["presentmon"] = summarize_presentmon(text)
        encoded = json.dumps(report, indent=2, allow_nan=False) + "\n"
        args.output.parent.mkdir(parents=True, exist_ok=True)
        with args.output.open("x", encoding="utf-8", newline="\n") as output:
            output.write(encoded)
        if "presentmon" in report:
            print(f"Recorded {args.run_id}: {report['presentmon']['present_rate_fps']:.3f} present/s from PresentMon")
        elif "cadence" in report:
            print(f"Recorded {args.run_id}: {report['cadence']['selected_window']['changed_rgb_per_s']:.3f} changed RGB/s in selected intervals")
        else:
            print(f"Recorded {args.run_id}: sanitized {len(report['source_sha256'])} hashed log source(s)")
        return 0
    except (OSError, ValueError, KeyError, OverflowError) as error:
        print(f"Log analysis failed: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())

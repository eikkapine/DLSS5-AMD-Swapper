#!/usr/bin/env python3
"""Summarize existing manual-run logs without loading a runtime or starting capture.

Uses Python's standard library. The JSON contains numeric aggregates and source
hashes, not raw log text, local paths, window titles, process IDs or adapter IDs.
Output is create-only so an earlier measurement cannot be silently replaced.
"""

from __future__ import annotations

import argparse
from collections import defaultdict
import datetime as dt
import hashlib
import json
import math
from pathlib import Path
import re
import sys
from typing import Any

MAX_LOG_BYTES = 8 * 1024 * 1024
APIS = ("launch", "device_wait", "stream_wait", "event_wait", "copy", "copy_async")


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


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cadence", required=True, type=Path)
    parser.add_argument("--hip", type=Path)
    parser.add_argument("--runtime-log", type=Path)
    parser.add_argument("--run-id", required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--bridge-sha256", required=True, help="Hash verified against this run's deployment record")
    parser.add_argument("--since", type=float, default=20.0)
    parser.add_argument("--until", type=float)
    parser.add_argument("--hip-since", type=float, default=20.0)
    parser.add_argument("--hip-until", type=float)
    parser.add_argument("--reported-base-fps", type=float)
    parser.add_argument("--framegen-multiplier", type=float)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    try:
        for value in (args.run_id, args.version):
            if not re.fullmatch(r"[A-Za-z0-9_.-]{1,80}", value):
                raise ValueError("Run ID and version must be short alphanumeric labels")
        if not re.fullmatch(r"[a-fA-F0-9]{64}", args.bridge_sha256):
            raise ValueError("Bridge SHA256 must contain 64 hexadecimal characters")
        for start, end in ((args.since, args.until), (args.hip_since, args.hip_until)):
            if not math.isfinite(start) or start < 0 or (end is not None and (not math.isfinite(end) or end <= start)):
                raise ValueError("Invalid measurement window")
        for value in (args.reported_base_fps, args.framegen_multiplier):
            if value is not None and (not math.isfinite(value) or value <= 0):
                raise ValueError("Reported rates and multipliers must be positive and finite")
        cadence, digest = read_log(args.cadence)
        report: dict[str, Any] = {
            "schema_version": 1,
            "run_id": args.run_id,
            "version": args.version,
            "bridge_sha256": args.bridge_sha256.lower(),
            "hash_association": "Provided by caller; verify with the deployment record, not the current executable alone",
            "analysis_created_at_utc": dt.datetime.now(dt.timezone.utc).isoformat(),
            "source_sha256": {"cadence": digest},
            "cadence": summarize_cadence(cadence, args.since, args.until),
            "manual_report": {
                "base_fps": args.reported_base_fps,
                "framegen_multiplier": args.framegen_multiplier,
                "nominal_output_fps": args.reported_base_fps * args.framegen_multiplier
                if args.reported_base_fps is not None and args.framegen_multiplier is not None else None,
                "measured_display_fps": None,
            },
            "limitations": [
                "Analysis reads existing logs only; no tests, playback or GPU work are started.",
                "Changed RGB submissions, HIP calls, neural jobs and displayed/generated FPS are distinct.",
                "Nominal output FPS is arithmetic, not an LSFG/display measurement.",
                "Window selection includes complete logged intervals only; endpoints are rounded.",
                "Whole-run averages can include bypass, strength changes and startup; effect state may be missing.",
                "Different gameplay runs are observational comparisons, not controlled A/B benchmarks.",
                "HIP device-wait duration includes queued work and contention, not isolated kernel time.",
                "HIP threads have overlapping windows and must not be summed as one critical path.",
            ],
        }
        if args.hip:
            text, digest = read_log(args.hip)
            report["source_sha256"]["hip"] = digest
            report["hip"] = summarize_hip(text, args.hip_since, args.hip_until)
        if args.runtime_log:
            text, digest = read_log(args.runtime_log)
            report["source_sha256"]["runtime"] = digest
            version = re.search(r"dlssnr_amd (v[\d.]+) \(build ([a-fA-F0-9]+)\)", text)
            samples = [{"job": int(job), "wall_ms": int(ms)} for job, ms in re.findall(
                r"network job (\d+) done in (\d+) ms", text
            )]
            report["runtime"] = {
                "version": version[1] if version else None,
                "build": version[2] if version else None,
                "sampled_jobs": samples,
                "observed_fault_lines": len(re.findall(r"(?im)^FAULT:", text)),
                "observed_gpu_error_lines": len(re.findall(r"(?im)^job \d+ GPU errors:", text)),
                "note": "Sparse job samples; marker counts are not a comprehensive runtime-health certification",
            }
        encoded = json.dumps(report, indent=2, allow_nan=False) + "\n"
        args.output.parent.mkdir(parents=True, exist_ok=True)
        with args.output.open("x", encoding="utf-8", newline="\n") as output:
            output.write(encoded)
        print(f"Recorded {args.run_id}: {report['cadence']['selected_window']['changed_rgb_per_s']:.3f} changed RGB/s in selected intervals")
        return 0
    except (OSError, ValueError, KeyError, OverflowError) as error:
        print(f"Log analysis failed: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())

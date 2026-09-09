#!/usr/bin/env python3
"""Summarize an existing HIP kernel-sampling log using only the standard library.

Accepts at most 8 MiB, 16,384 physical records and 4,096 kernel samples. The
create-only JSON requires the run's explicit version and deployment SHA256.
Only sanitized identities, relative module offsets and numeric aggregates are
exported. Dispatch medians are ranked; samples are never added into an inference
time. This script does not load a GPU runtime, start capture or run playback.
"""

from __future__ import annotations

import argparse
from collections import Counter, defaultdict
from dataclasses import dataclass
import datetime as dt
import hashlib
import io
import json
import math
import os
from pathlib import Path
import re
import stat
import statistics
import sys
from typing import Any

MAX_LOG_BYTES = 8 * 1024 * 1024
MAX_RECORDS = 16_384  # Includes headers, blank lines and ignored host intervals.
MAX_KERNEL_SAMPLES = 4_096
MAX_LINE_CHARS = 8_192
UINT32_MAX = (1 << 32) - 1
UINT64_MAX = (1 << 64) - 1
# StartHipHostTiming rejects larger neural module images. Never export a VA.
MAX_MODULE_BYTES = 256 * 1024 * 1024
LABEL_PATTERN = r"[A-Za-z0-9][A-Za-z0-9_.-]{0,79}"

SAMPLING_STATUSES = frozenset({
    "bounded_timed_launch", "unavailable_exports", "disabled_by_option",
})
DISABLE_REASONS = frozenset({
    "device_changed_or_unavailable", "existing_device_wait_failed",
    "completion_device_changed_or_unavailable",
    "elapsed_time_unavailable", "symbol_lookup_failed", "worker_limit",
    "event_initialization_failed", "timed_launch_failed",
})
KERNEL_FIELDS = frozenset({
    "elapsed_s", "ordinal", "launches_in_wait", "kernel", "kernel_rva",
    "rva_valid", "grid_x", "grid_y", "grid_z", "block_x", "block_y",
    "block_z", "shared_bytes", "gpu_ms", "stream_default",
    "sampling_attempts", "sampling_dropped",
})
HEADER_FIELDS = frozenset({
    "kernel_sampling", "since_s", "until_s", "max_samples", "max_workers",
    "events_per_worker",
})


@dataclass(frozen=True, slots=True)
class Dispatch:
    launches_in_wait: int
    ordinal: int
    kernel: str | None
    kernel_rva: int | None
    grid: tuple[int, int, int]
    block: tuple[int, int, int]
    shared_bytes: int


def read_log(path: Path) -> tuple[str, str, int, bool]:
    try:
        with path.open("rb") as stream:
            if not stat.S_ISREG(os.fstat(stream.fileno()).st_mode):
                raise ValueError("Input must be a regular log file")
            raw = stream.read(MAX_LOG_BYTES + 1)
    except OSError as error:
        raise ValueError(f"Cannot read input log (OS error {error.errno})") from None
    if len(raw) > MAX_LOG_BYTES:
        raise ValueError("Input log exceeds the 8 MiB limit")
    try:
        text = raw.decode("utf-8-sig")
    except UnicodeDecodeError:
        raise ValueError("Input log is not valid UTF-8") from None
    # Hash the exact bytes, including any BOM and original line endings.
    return text, hashlib.sha256(raw).hexdigest(), len(raw), raw.endswith(b"\n")


def fields(payload: str, allowed: frozenset[str]) -> dict[str, str]:
    result: dict[str, str] = {}
    for token in payload.split():
        key, separator, value = token.partition("=")
        if not separator:
            raise ValueError("Malformed telemetry field")
        if key not in allowed:
            continue  # In particular, do not retain process/thread IDs or paths.
        if key in result:
            raise ValueError("Duplicate telemetry field")
        result[key] = value
    return result


def required(values: dict[str, str], key: str) -> str:
    if key not in values:
        raise ValueError(f"Missing required {key} field")
    return values[key]


def integer(
    values: dict[str, str], key: str, minimum: int = 0,
    maximum: int = UINT32_MAX,
) -> int:
    text = required(values, key)
    if not re.fullmatch(r"-?[0-9]{1,20}", text):
        raise ValueError(f"Invalid integer field {key}")
    value = int(text)
    if not minimum <= value <= maximum:
        raise ValueError(f"Out-of-range integer field {key}")
    return value


def nonnegative_float(values: dict[str, str], key: str) -> float:
    text = required(values, key)
    if len(text) > 64 or not re.fullmatch(r"[0-9]+(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?", text):
        raise ValueError(f"Invalid numeric field {key}")
    value = float(text)
    if not math.isfinite(value) or value < 0:
        raise ValueError(f"Field {key} must be finite and nonnegative")
    return value


def sanitized_kernel(text: str) -> str | None:
    # The producer already restricts names to this alphabet and 255 characters.
    # Unexpected path-like text or apparent pointers are redacted, not echoed.
    if not re.fullmatch(r"[A-Za-z0-9_]{1,255}", text):
        return None
    if (text.isdecimal() or re.search(r"0[xX][0-9A-Fa-f]+", text)
            or re.fullmatch(r"[0-9A-Fa-f]{8,16}", text)):
        return None
    return text


def dimensions(values: dict[str, str], prefix: str) -> tuple[int, int, int]:
    return (
        integer(values, prefix + "_x", minimum=1),
        integer(values, prefix + "_y", minimum=1),
        integer(values, prefix + "_z", minimum=1),
    )


def sampling_header(payload: str) -> dict[str, Any]:
    values = fields(payload, HEADER_FIELDS)
    status = required(values, "kernel_sampling")
    since = nonnegative_float(values, "since_s")
    until = nonnegative_float(values, "until_s")
    if until <= since:
        raise ValueError("Invalid sampling window in header")
    return {
        "status": status if status in SAMPLING_STATUSES else "unrecognized",
        "since_s": since,
        "until_s": until,
        "max_samples": integer(values, "max_samples", minimum=1),
        "max_workers": integer(values, "max_workers", minimum=1),
        "events_per_worker": integer(values, "events_per_worker", minimum=1),
    }


def kernel_sample(payload: str) -> tuple[Dispatch, float, float, int, int]:
    values = fields(payload, KERNEL_FIELDS)
    launches = integer(values, "launches_in_wait", minimum=1)
    ordinal = integer(values, "ordinal")
    if ordinal >= launches:
        raise ValueError("Kernel ordinal is outside its recorded sequence length")
    rva_valid = integer(values, "rva_valid", maximum=1)
    rva = integer(values, "kernel_rva", maximum=MAX_MODULE_BYTES - 1) if rva_valid else None
    # Invalid RVAs are never exported, even when a numeric address is present.
    integer(values, "stream_default", minimum=1, maximum=1)
    dispatch = Dispatch(
        launches, ordinal, sanitized_kernel(required(values, "kernel")), rva,
        dimensions(values, "grid"), dimensions(values, "block"),
        integer(values, "shared_bytes", maximum=UINT64_MAX),
    )
    return (
        dispatch, nonnegative_float(values, "gpu_ms"),
        nonnegative_float(values, "elapsed_s"),
        integer(values, "sampling_attempts", minimum=1),
        integer(values, "sampling_dropped"),
    )


def duration_statistics(samples: list[float]) -> dict[str, float]:
    ordered = sorted(samples)
    low = ordered[(len(ordered) - 1) // 2]
    high = ordered[len(ordered) // 2]
    return {
        # This equivalent midpoint avoids overflow for large finite durations.
        "median": low + (high - low) / 2,
        "min": ordered[0],
        "max": ordered[-1],
        "mean": statistics.mean(ordered),
    }


def ordinal_coverage(launches: int, counts: Counter[int]) -> dict[str, Any]:
    observed = sorted(counts)
    missing_ranges: list[list[int]] = []
    next_ordinal = 0
    for ordinal in observed:
        if ordinal > next_ordinal:
            missing_ranges.append([next_ordinal, ordinal - 1])
        next_ordinal = ordinal + 1
    if next_ordinal < launches:
        missing_ranges.append([next_ordinal, launches - 1])
    # Ranges, rather than range(launches), keep allocation bounded by samples
    # even if a later worker interval contains a very large sequence length.
    return {
        "launches_in_wait": launches,
        "sample_count": sum(counts.values()),
        "observed_ordinal_count": len(observed),
        "observed_ordinals": observed,
        "missing_ordinal_count": launches - len(observed),
        "missing_ordinal_ranges_inclusive": missing_ranges,
        "ordinal_coverage_fraction": len(observed) / launches,
        "all_ordinals_observed_across_samples": len(observed) == launches,
    }


def summarize(text: str) -> dict[str, Any]:
    groups: dict[Dispatch, list[float]] = defaultdict(list)
    coverage: dict[int, Counter[int]] = defaultdict(Counter)
    disabled: Counter[tuple[str, int]] = Counter()
    source_header: dict[str, Any] | None = None
    header: dict[str, Any] | None = None
    logged_version: str | None = None
    line_count = ignored_count = sample_count = redacted_count = 0
    elapsed_min: float | None = None
    elapsed_max: float | None = None
    attempts_max: int | None = None
    dropped_max: int | None = None
    seen_data = False
    # Iterate instead of splitlines(): millions of blank lines cannot allocate
    # millions of record objects before the record limit is enforced.
    for line_count, raw_line in enumerate(io.StringIO(text), start=1):
        if line_count > MAX_RECORDS:
            raise ValueError("Input exceeds the 16,384 physical-record limit")
        if len(raw_line) > MAX_LINE_CHARS:
            raise ValueError(f"Line {line_count} exceeds the 8,192-character limit")
        line = raw_line.strip()
        parts = line.split(maxsplit=1)
        kind = parts[0] if parts else ""
        payload = parts[1] if len(parts) == 2 else ""
        try:
            if kind.startswith("bridge_version="):
                if logged_version is not None or seen_data:
                    raise ValueError("Repeated or late version header; select one unmixed log")
                values = fields(line, frozenset({"bridge_version"}))
                logged_version = required(values, "bridge_version")
                if not re.fullmatch(LABEL_PATTERN, logged_version):
                    raise ValueError("Invalid bridge version label in header")
            elif kind.startswith("schema="):
                if source_header is not None or seen_data:
                    raise ValueError("Repeated or late source header; select one unmixed log")
                values = fields(line, frozenset({"schema", "capture_limit_s"}))
                schema = integer(values, "schema")
                if schema != 1:
                    raise ValueError("Unsupported HIP log schema; expected schema 1")
                source_header = {
                    "schema": schema,
                    "capture_limit_s": nonnegative_float(values, "capture_limit_s"),
                }
            elif kind.startswith("kernel_sampling="):
                if header is not None or seen_data:
                    raise ValueError("Repeated or late sampling header; select one unmixed log")
                header = sampling_header(line)
            elif kind == "gpu_kernel":
                seen_data = True
                sample_count += 1
                if sample_count > MAX_KERNEL_SAMPLES:
                    raise ValueError("Input exceeds the 4,096 kernel-sample limit")
                dispatch, duration, elapsed, attempts, dropped = kernel_sample(payload)
                groups[dispatch].append(duration)
                coverage[dispatch.launches_in_wait][dispatch.ordinal] += 1
                redacted_count += dispatch.kernel is None
                elapsed_min = elapsed if elapsed_min is None else min(elapsed_min, elapsed)
                elapsed_max = elapsed if elapsed_max is None else max(elapsed_max, elapsed)
                attempts_max = attempts if attempts_max is None else max(attempts_max, attempts)
                dropped_max = dropped if dropped_max is None else max(dropped_max, dropped)
            elif kind == "gpu_sampling_disabled":
                seen_data = True
                values = fields(payload, frozenset({"reason", "status"}))
                reason = required(values, "reason")
                reason = reason if reason in DISABLE_REASONS else "unrecognized"
                status = integer(values, "status", minimum=-(1 << 31), maximum=(1 << 31) - 1)
                disabled[reason, status] += 1
            else:
                ignored_count += 1
                seen_data = seen_data or kind.startswith("elapsed_s=")
        except ValueError as error:
            # Errors name fields/line numbers, never raw tokens or log text.
            raise ValueError(f"Line {line_count}: {error}") from None
    if source_header is None and header is None and logged_version is None and not groups and not disabled:
        raise ValueError("No HIP timing header, sampling header or kernel telemetry found")

    dispatches = []
    for dispatch, samples in groups.items():
        dispatches.append({
            "launches_in_wait": dispatch.launches_in_wait,
            "ordinal": dispatch.ordinal,
            "kernel": dispatch.kernel,
            "kernel_rva": dispatch.kernel_rva,
            "rva_valid": dispatch.kernel_rva is not None,
            "identity_limited": dispatch.kernel in (None, "unresolved") and dispatch.kernel_rva is None,
            "grid": list(dispatch.grid),
            "block": list(dispatch.block),
            "shared_bytes": dispatch.shared_bytes,
            "count": len(samples),
            "gpu_ms": duration_statistics(samples),
        })
    dispatches.sort(key=lambda row: (
        -row["gpu_ms"]["median"], row["launches_in_wait"], row["ordinal"],
        row["kernel"] or "", -1 if row["kernel_rva"] is None else row["kernel_rva"],
        row["grid"], row["block"], row["shared_bytes"],
    ))
    known_header_status = header is not None and header["status"] in SAMPLING_STATUSES
    return {
        "bridge_version_from_header": logged_version,
        "source_header": source_header,
        "sampling_header": header,
        "header_status_conflicts_with_samples": (
            sample_count > 0 and header["status"] != "bounded_timed_launch"
        ) if known_header_status else None,
        "record_counts": {
            "physical_records": line_count,
            "gpu_kernel": sample_count,
            "gpu_sampling_disabled": sum(disabled.values()),
            "ignored_records": ignored_count,
            "kernel_names_redacted": redacted_count,
        },
        "sample_availability": "observed" if sample_count else "no_gpu_kernel_rows",
        "sample_completion_elapsed_s_range": [elapsed_min, elapsed_max] if sample_count else None,
        "sampling_attempts_max_observed": attempts_max,
        "sampling_dropped_max_observed": dropped_max,
        "missing_gpu_sample_count": None,
        "disable_events": [
            {"reason": reason, "status": status, "count": count}
            for (reason, status), count in sorted(disabled.items())
        ],
        "ordinal_base": 0,
        "dimension_order": ["x", "y", "z"],
        "sort_order": "gpu_ms.median descending per dispatch group",
        "dispatches": dispatches,
        "coverage_by_sequence_length": [
            ordinal_coverage(launches, counts) for launches, counts in sorted(coverage.items())
        ],
        "end_to_end_inference_ms": None,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, allow_abbrev=False)
    parser.add_argument("--hip", required=True, type=Path, help="Existing, settled HIP timing log")
    parser.add_argument("--run-id", required=True, help="Short nonprivate run label")
    parser.add_argument("--version", required=True, help="Version actually deployed for this run")
    parser.add_argument("--bridge-sha256", required=True, help="SHA256 verified against this run's deployment record")
    parser.add_argument("--output", required=True, type=Path, help="New JSON file; parent directory must exist")
    args = parser.parse_args()
    try:
        for label in (args.run_id, args.version):
            if not re.fullmatch(LABEL_PATTERN, label):
                raise ValueError("Run ID and version must be 1-80 character alphanumeric labels with '.', '_' or '-'")
        if not re.fullmatch(r"[a-fA-F0-9]{64}", args.bridge_sha256):
            raise ValueError("Bridge SHA256 must contain exactly 64 hexadecimal characters")
        text, digest, byte_count, terminated = read_log(args.hip)
        kernels = summarize(text)
        logged_version = kernels["bridge_version_from_header"]
        if logged_version is not None and logged_version != args.version:
            raise ValueError("Supplied version disagrees with the log header; check this run's deployment record")
        report = {
            "schema_version": 1,
            "run_id": args.run_id,
            "version": args.version,
            "version_matches_header": logged_version == args.version if logged_version is not None else None,
            "bridge_sha256": args.bridge_sha256.lower(),
            "hash_association": "Caller-provided deployment identity; verify against this run's deployment record, not the current installed executable",
            "analysis_created_at_utc": dt.datetime.now(dt.timezone.utc).isoformat(),
            "source_sha256": {"hip": digest},
            "source_bytes": byte_count,
            "source_ends_with_newline": terminated,
            "analysis_limits": {
                "input_bytes": MAX_LOG_BYTES, "physical_records": MAX_RECORDS,
                "kernel_samples": MAX_KERNEL_SAMPLES, "line_characters": MAX_LINE_CHARS,
            },
            "kernels": kernels,
            "limitations": [
                "Sparse dispatch samples come from different worker intervals and frames; they are not a complete inference timeline.",
                "Timestamp instrumentation and host timing can perturb scheduling; observed event durations are not uninstrumented performance.",
                "GPU dispatch time is not end-to-end inference, host wait time, neural job throughput or display FPS; dispatch durations are never summed here.",
                "Ordinal coverage is the union across sampled intervals for each observed sequence length, not coverage of one frame or all workers.",
                "Only sequence lengths present in kernel rows are known. Missing ordinals have no invented duration; no kernel rows means unknown coverage.",
                "Sampling header limits are eligibility/cap information, not promised sample counts; a missing header remains null.",
                "Attempt/drop counters are cumulative snapshots: maxima are not final totals. Drops can include disable messages, so attempts minus rows is not a missing-sample count.",
                "The log can stop before pending records are drained. A final newline does not prove a complete or settled recording; final missing-sample totals remain unknown.",
                "Disable counts cover recorded messages only, not unique workers or all failures; numeric status codes are retained, unknown reason labels are redacted.",
                "Only default-stream samples in named neural-module imports are represented. Different gameplay runs are not controlled A/B comparisons.",
                "Kernel names can be sanitized or truncated and may collide; unresolved/redacted names without a valid module RVA do not establish unique kernel identity.",
                "Valid RVAs are relative to the neural module and meaningful only with the associated deployment; process/thread identifiers, paths and raw lines are not exported.",
                "A recorded bridge version must match the caller's version. The deployment SHA256 is caller-verified; it cannot be derived from kernel rows.",
            ],
        }
        # Serialize before creating the destination; reject non-finite JSON.
        encoded = json.dumps(report, indent=2, allow_nan=False) + "\n"
        try:
            # Exclusive creation is the overwrite guard, including races/symlinks.
            with args.output.open("x", encoding="utf-8", newline="\n") as output:
                output.write(encoded)
        except FileExistsError:
            raise ValueError("Output already exists; choose a new filename. No measurement was overwritten") from None
        except OSError as error:
            raise ValueError(
                f"Cannot write new JSON output (OS error {error.errno}); check the parent directory. A partial new file may remain"
            ) from None
        print(f"Recorded {args.run_id}: {kernels['record_counts']['gpu_kernel']} GPU kernel samples in {len(kernels['dispatches'])} dispatch groups")
        return 0
    except (ValueError, OverflowError) as error:
        print(f"Kernel log analysis failed: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())

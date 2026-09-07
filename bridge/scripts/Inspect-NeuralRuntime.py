#!/usr/bin/env python3
"""Inspect neural-runtime files without loading a DLL or submitting GPU work.

Requires the existing pefile and msgpack Python packages. Output contains only
metadata, import names and selected log measurements, never executable payloads.
Run with --help for usage. This is static inspection, not a performance test.
"""

from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import json
from pathlib import Path
import re
import struct
import sys
from typing import Any


KERNEL_FIELDS = (
    ".name", ".symbol", ".vgpr_count", ".sgpr_count",
    ".group_segment_fixed_size", ".private_segment_fixed_size",
    ".max_flat_workgroup_size", ".wavefront_size",
)


def checked_region(data: bytes, start: int, size: int) -> bytes:
    if start < 0 or size < 0 or start > len(data) - size:
        raise ValueError("Metadata region extends outside the input file")
    return data[start:start + size]


def read_code_objects(blob: bytes, msgpack: Any) -> list[dict[str, Any]]:
    objects = []
    for marker in re.finditer(rb"\x7fELF", blob):
        offset = marker.start()
        elf = blob[offset:]
        if len(elf) < 64 or elf[4:6] != b"\x02\x01":
            continue
        section_offset = struct.unpack_from("<Q", elf, 40)[0]
        entry_size, count = struct.unpack_from("<HH", elf, 58)
        if entry_size < 64 or count == 0 or count > 512:
            continue
        if section_offset + entry_size * count > len(elf):
            continue
        for index in range(count):
            header = struct.unpack_from(
                "<IIQQQQIIQQ", elf, section_offset + index * entry_size
            )
            if header[1] != 7:  # SHT_NOTE
                continue
            notes = checked_region(elf, header[4], header[5])
            position = 0
            while position + 12 <= len(notes):
                name_size, descriptor_size, kind = struct.unpack_from(
                    "<III", notes, position
                )
                position += 12
                name = checked_region(notes, position, name_size).rstrip(b"\0")
                position += (name_size + 3) & ~3
                descriptor = checked_region(notes, position, descriptor_size)
                position += (descriptor_size + 3) & ~3
                if name != b"AMDGPU" or kind != 32:
                    continue
                metadata = msgpack.unpackb(descriptor, raw=False)
                kernels = metadata.get("amdhsa.kernels", [])
                objects.append({
                    "file_offset": offset,
                    "target": metadata.get("amdhsa.target"),
                    "kernel_count": len(kernels),
                    "kernels": [
                        {key: kernel.get(key) for key in KERNEL_FIELDS}
                        for kernel in kernels
                    ],
                })
    return objects


def inspect(runtime: Path, log: Path | None) -> dict[str, Any]:
    try:
        import pefile
        import msgpack
    except ImportError as error:
        raise RuntimeError(
            "This inspector requires pefile and msgpack in the selected Python environment"
        ) from error
    if runtime.stat().st_size > 256 * 1024 * 1024:
        raise ValueError("Runtime exceeds the 256 MiB static-inspection limit")
    blob = runtime.read_bytes()
    with pefile.PE(data=blob) as pe:
        imports = []
        for table in ("DIRECTORY_ENTRY_IMPORT", "DIRECTORY_ENTRY_DELAY_IMPORT"):
            for library in getattr(pe, table, []):
                names = sorted({
                    item.name.decode("ascii", errors="replace")
                    for item in library.imports if item.name
                })
                imports.append({
                    "library": library.dll.decode("ascii", errors="replace"),
                    "table": table,
                    "names": names,
                })
    hip_names = sorted({
        name for library in imports for name in library["names"]
        if name.startswith("hip") or name.startswith("__hip")
    })
    timing_names = sorted({
        name for library in imports for name in library["names"]
        if re.search(r"TickCount|PerformanceCounter|PerformanceFrequency|Sleep|WaitFor", name)
    })
    report: dict[str, Any] = {
        "schema_version": 1,
        "inspected_at_utc": dt.datetime.now(dt.timezone.utc).isoformat(),
        "runtime_filename": runtime.name,
        "runtime_sha256": hashlib.sha256(blob).hexdigest(),
        "runtime_bytes": len(blob),
        "hip_imports": hip_names,
        "graph_api_imports": [name for name in hip_names if "Graph" in name or "Capture" in name],
        "timing_imports": timing_names,
        "code_objects": read_code_objects(blob, msgpack),
        "limits": [
            "The DLL was read as data, not loaded or executed.",
            "Imports do not establish runtime call frequency or dynamically resolved APIs.",
            "Kernel metadata does not establish which kernels ran or their elapsed GPU time.",
            "Maximum workgroup size is a compiler bound, not an observed launch size.",
            "Register and shared-memory counts alone do not establish achieved occupancy.",
            "Logged job duration is not unique displayed FPS or isolated GPU kernel time.",
        ],
    }
    if log is not None:
        with log.open("rb") as stream:
            raw_log = stream.read(8 * 1024 * 1024 + 1)
        if len(raw_log) > 8 * 1024 * 1024:
            raise ValueError("Log exceeds the 8 MiB inspection limit; select an archived bounded log")
        text = raw_log.decode("utf-8", errors="replace")
        jobs = [
            {"job": int(job), "milliseconds": int(milliseconds)}
            for job, milliseconds in re.findall(r"network job (\d+) done in (\d+) ms", text)
        ]
        report["log"] = {
            "sha256": hashlib.sha256(raw_log).hexdigest(),
            "sample_count": len(jobs),
            "last_samples": jobs[-32:],
            "shared_inputs_and_output_reported": bool(re.search(
                r"inputs shared \(zero-copy\), output shared \(zero-copy\)", text
            )),
            "async_mode_reported": "mode async" in text,
            "source_dimensions": re.findall(r"swapchain (\d+x\d+) format", text),
        }
    return report


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("runtime", type=Path, help="AMD runtime DLL to inspect as data")
    parser.add_argument("--log", type=Path, help="Existing runtime log; does not start capture")
    parser.add_argument("--output", type=Path, help="New JSON report path; refuses to overwrite")
    args = parser.parse_args()
    try:
        report = inspect(args.runtime, args.log)
        encoded = json.dumps(report, indent=2, ensure_ascii=True) + "\n"
        if args.output is None:
            sys.stdout.write(encoded)
        else:
            with args.output.open("x", encoding="utf-8", newline="\n") as stream:
                stream.write(encoded)
            print(f"Static metadata report: {args.output}")
        return 0
    except (OSError, ValueError, RuntimeError, struct.error) as error:
        print(f"Inspection failed: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())

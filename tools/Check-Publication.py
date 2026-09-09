#!/usr/bin/env python3
"""Fail if public docs/measurements contain untraceable performance claims."""

from __future__ import annotations

import json
from pathlib import Path
import re
import sys


ROOT = Path(__file__).resolve().parents[1]
TEXT_EXTENSIONS = {".md", ".json", ".txt"}
FORBIDDEN_PHRASES = (
    "user-observed",
    "user observed",
    "user-reported",
    "user reported",
    "user says",
    "i observed",
    "manual_report",
    "reported-base-fps",
    "framegen-multiplier",
    "observed_output_source",
)
FPS_NUMBER = re.compile(r"(?i)(?:~|about\s+|roughly\s+)?\d+(?:\.\d+)?\s*fps\b")


def main() -> int:
    failures: list[str] = []
    for path in ROOT.rglob("*"):
        if not path.is_file() or path.suffix.lower() not in TEXT_EXTENSIONS:
            continue
        relative = path.relative_to(ROOT)
        if any(part in {".git", "runs", "build", "runtime"} for part in relative.parts):
            continue
        text = path.read_text(encoding="utf-8", errors="replace")
        lower = text.lower()
        for phrase in FORBIDDEN_PHRASES:
            if phrase in lower:
                failures.append(f"{relative}: forbidden provenance phrase/field: {phrase}")

        # Historical/public prose may describe FPS conceptually, but a concrete
        # performance number must live in a machine-produced measurement JSON.
        if path.suffix.lower() != ".json" and FPS_NUMBER.search(text):
            failures.append(f"{relative}: numeric FPS claim outside measurement JSON")

        if relative.parts[:2] == ("docs", "measurements") and path.suffix.lower() == ".json":
            try:
                data = json.loads(text)
            except json.JSONDecodeError as error:
                failures.append(f"{relative}: invalid JSON: {error}")
                continue
            if data.get("schema_version", 0) < 2:
                failures.append(f"{relative}: measurement schema_version must be >= 2")
            sources = data.get("source_sha256")
            if not isinstance(sources, dict) or not sources:
                failures.append(f"{relative}: missing source_sha256")
            elif any(not re.fullmatch(r"[0-9a-fA-F]{64}", str(value)) for value in sources.values()):
                failures.append(f"{relative}: invalid source SHA-256")

    if failures:
        print("Publication provenance check failed:", file=sys.stderr)
        for failure in failures:
            print(f"- {failure}", file=sys.stderr)
        return 1
    print("Publication provenance check passed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

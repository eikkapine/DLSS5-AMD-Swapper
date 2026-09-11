#!/usr/bin/env python3
"""Direct-game DLSS Neural Rendering installer helper for AMD FSR/DX12 games.

This project does not distribute DLSS-NR-on-AMD or NVIDIA model/runtime files.
The user supplies both files locally. The helper verifies the upstream setup
binary against GitHub's release digest, checks the target game, performs a
reversible install, and records a local manifest for diagnostics/removal.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import struct
import subprocess
import sys
import tempfile
import time
from typing import Any
import urllib.request


UPSTREAM_API = "https://api.github.com/repos/danielblnc/DLSS-NR-on-AMD/releases/latest"
UPSTREAM_RELEASES = "https://github.com/danielblnc/DLSS-NR-on-AMD/releases"
UPSTREAM_ASSET = "dlssnr_on_amd_setup.exe"
MANIFEST_NAME = ".dlss5-amd-swapper.json"
LEGACY_MANIFEST_NAME = ".nr-auto-scale-direct.json"

KNOWN_PROXY_NAMES = ("version.dll", "winmm.dll", "dbghelp.dll", "wininet.dll", "winhttp.dll", "dxgi.dll")
KNOWN_RUNTIME_FILES = (
    "dlssnr_on_amd.ini",
    "dlssnr_on_amd_weights.bin",
    "dlssnr_on_amd.log",
)
FSR_MARKERS = {
    "ffx_fsr3upscaler_x64.dll",
    "ffx_fsr3_x64.dll",
    "amd_fidelityfx_dx12.dll",
    "amd_fidelityfx_upscaler_dx12.dll",
    "amd_fidelityfx_loader_dx12.dll",
    "ffx_fsr2_api_x64.dll",
}
ANTI_CHEAT_MARKERS = {
    "easyanticheat",
    "easyanticheat_eos",
    "battleye",
    "beservice",
    "vgc",
    "vgk",
    "faceit",
    "faceitclient",
    "ace-base",
    "ace-guard",
    "equ8",
    "eaanticheat",
    "javelinanticheat",
    "ricochet",
    "xigncode",
    "gameguard",
    "nprotect",
    "punkbuster",
    "pbsvc",
    "blackcipher",
}

ROUTE_POST_FSR = "amd-fsr-direct"
ROUTE_OPTISCALER = "amd-optiscaler-presr"
OPTI_PROXY_NAMES = ("dxgi.dll", "version.dll", "winmm.dll", "dbghelp.dll", "wininet.dll", "winhttp.dll")
OPTI_PASS_NAMES = ("dlssnr_amd_pass1.dll", "dlssnr_amd_pass2.dll", "dlssnr_amd_pass3.dll")
OPTI_ROOT_MANAGED = ("OptiScaler.ini", "OptiScaler.log", "amd_presr.log", *OPTI_PASS_NAMES, "dlssnr_on_amd_weights.bin")
OPTI_WEIGHTS = "dlssnr_on_amd_weights.bin"
OPTI_ENABLER = "dlss-enabler-headless.dll"
OPTI_DEPENDENCY_FOLDER = "OptiScaler"
OPTI_REQUIRED_UPSCALER = "amd_fidelityfx_upscaler_dx12.dll"
OPTI_FORK_MARKER = "amd-presr"
OPTI_PASS_MARKER = b"dlssnr_amd"


def sha256(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def pe_machine(path: Path) -> int | None:
    try:
        with path.open("rb") as stream:
            if stream.read(2) != b"MZ":
                return None
            stream.seek(0x3C)
            offset = struct.unpack("<I", stream.read(4))[0]
            stream.seek(offset)
            if stream.read(4) != b"PE\0\0":
                return None
            return struct.unpack("<H", stream.read(2))[0]
    except (OSError, struct.error):
        return None


def file_state(path: Path) -> dict[str, Any] | None:
    if not path.is_file():
        return None
    stat = path.stat()
    return {"size": stat.st_size, "sha256": sha256(path)}


def is_real_weights(path: Path) -> bool:
    if not path.is_file() or path.stat().st_size <= 1024 * 1024:
        return False
    with path.open("rb") as stream:
        return not stream.read(32).startswith(b"version https://git-lfs")


def contains_marker(path: Path, marker: bytes) -> bool:
    carry = b""
    with path.open("rb") as stream:
        while True:
            chunk = stream.read(4 * 1024 * 1024)
            if not chunk:
                return False
            data = carry + chunk
            if marker in data:
                return True
            carry = data[-(len(marker) - 1):] if len(marker) > 1 else b""


def pe_product_version(path: Path) -> tuple[str | None, str | None]:
    """Read ProductName/ProductVersion from the VS_VERSIONINFO string table without Win32 APIs."""
    data = path.read_bytes()
    marker = "VS_VERSION_INFO".encode("utf-16le")
    start = data.find(marker)
    if start < 0:
        return None, None
    block = data[start:start + 8192]

    def read_value(key: str) -> str | None:
        needle = key.encode("utf-16le") + b"\x00\x00"
        index = block.find(needle)
        if index < 0:
            return None
        cursor = index + len(needle)
        while cursor < len(block) and block[cursor:cursor + 2] == b"\x00\x00":
            cursor += 2
        end = block.find(b"\x00\x00", cursor)
        if end < 0:
            return None
        while end % 2:
            end = block.find(b"\x00\x00", end + 1)
            if end < 0:
                return None
        return block[cursor:end].decode("utf-16le", errors="replace").strip("\x00") or None

    return read_value("ProductName"), read_value("ProductVersion")


def parse_sha256sums(path: Path) -> list[tuple[str, str]]:
    entries: list[tuple[str, str]] = []
    for raw in path.read_text(encoding="utf-8", errors="replace").splitlines():
        line = raw.strip()
        if len(line) < 66 or not all(ch in "0123456789abcdefABCDEF" for ch in line[:64]):
            continue
        entries.append((line[64:].lstrip(" *").replace("/", "\\"), line[:64].lower()))
    return entries


def validate_package(root: Path, version_reader=None) -> dict[str, Any]:
    version_reader = version_reader or pe_product_version
    root = root.resolve()
    if not root.is_dir():
        raise RuntimeError("The OptiScaler package folder does not exist")
    if (root / "OptiScaler.dll").is_file():
        fork, layout = root / "OptiScaler.dll", "package"
    elif (root / "dxgi.dll").is_file():
        fork, layout = root / "dxgi.dll", "vodkaman"
    else:
        raise RuntimeError("No OptiScaler.dll or dxgi.dll was found in the package folder")
    if pe_machine(fork) != 0x8664:
        raise RuntimeError(f"{fork.name} is not a 64-bit Windows PE file")
    product, version = version_reader(fork)
    if (product or "").lower() != "optiscaler":
        raise RuntimeError(f"{fork.name} does not identify itself as OptiScaler")
    if not version or OPTI_FORK_MARKER not in version.lower():
        raise RuntimeError("This OptiScaler build is not the AMD pre-SR fork; the pre-SR route needs a build whose version contains amd-presr")

    passes: list[Path] = []
    for name in OPTI_PASS_NAMES:
        candidate = root / name
        if not candidate.is_file():
            if not passes:
                raise RuntimeError("dlssnr_amd_pass1.dll was not found in the package folder")
            continue
        if pe_machine(candidate) != 0x8664:
            raise RuntimeError(f"{name} is not a 64-bit Windows PE file")
        if not contains_marker(candidate, OPTI_PASS_MARKER):
            raise RuntimeError(f"{name} does not look like a DLSS-NR-on-AMD runtime (marker missing)")
        passes.append(candidate)

    files: dict[str, dict[str, Any]] = {}

    def record(path: Path) -> None:
        files[str(path.relative_to(root))] = file_state(path)

    record(fork)
    for item in passes:
        record(item)
    ini = root / "OptiScaler.ini"
    enabler = root / OPTI_ENABLER
    deps = root / OPTI_DEPENDENCY_FOLDER
    sums = root / "SHA256SUMS.txt"
    if ini.is_file():
        record(ini)
    if enabler.is_file():
        record(enabler)
    if deps.is_dir():
        for path in sorted(deps.rglob("*")):
            if path.is_file():
                record(path)
    sums_verified = False
    if sums.is_file():
        for relative, expected in parse_sha256sums(sums):
            full = (root / relative).resolve()
            if root not in full.parents:
                raise RuntimeError(f"SHA256SUMS.txt lists a path outside the package: {relative}")
            if not full.is_file():
                continue
            actual = files.get(str(full.relative_to(root)), {}).get("sha256") or sha256(full)
            if actual != expected:
                raise RuntimeError(f"SHA256SUMS.txt does not match {relative}. Re-download the package before installing")
        sums_verified = True
    weights = root / OPTI_WEIGHTS
    return {
        "root": str(root),
        "layout": layout,
        "optiscaler_dll": str(fork),
        "pass_dlls": [str(item) for item in passes],
        "ini": str(ini) if ini.is_file() else None,
        "dependency_folder": str(deps) if deps.is_dir() else None,
        "enabler": str(enabler) if enabler.is_file() else None,
        "weights": str(weights) if is_real_weights(weights) else None,
        "fork_version": version,
        "files": files,
        "sha256sums_verified": sums_verified,
    }


def find_local_weights(configured: Path | None, game_dirs: list[Path], lossless: Path | None) -> dict[str, Any] | None:
    candidates: list[Path] = []
    if configured:
        candidates.append(configured)
    if lossless:
        candidates.append(lossless / "nr-bridge" / "runtime" / OPTI_WEIGHTS)
        candidates.append(lossless / OPTI_WEIGHTS)
    candidates.extend(directory / OPTI_WEIGHTS for directory in game_dirs)
    chosen: dict[str, Any] | None = None
    seen: set[str] = set()
    for candidate in candidates:
        key = str(candidate.resolve()).lower()
        if key in seen or not is_real_weights(candidate):
            continue
        seen.add(key)
        state = {"source": str(candidate.resolve()), **file_state(candidate)}
        if chosen is None:
            chosen = state
        elif chosen["sha256"] != state["sha256"]:
            raise RuntimeError(f"Two local weights files differ: {chosen['source']} and {state['source']}")
    return chosen


MINIMAL_INI_HEADER = "; Written by DLSS5 AMD Swapper. Unlisted OptiScaler keys keep their defaults.\n; Open the in-game OptiScaler menu (Insert) to change anything else.\n"


def _ini_set(lines: list[str], section: str, key: str, value: str) -> None:
    section_index = next((i for i, line in enumerate(lines) if line.strip().lower() == f"[{section.lower()}]"), -1)
    if section_index < 0:
        if lines and lines[-1].strip():
            lines.append("")
        lines.extend([f"[{section}]", f"{key}={value}"])
        return
    end = len(lines)
    for i in range(section_index + 1, len(lines)):
        stripped = lines[i].strip()
        if stripped.startswith("[") and stripped.endswith("]"):
            end = i
            break
        if "=" in lines[i] and lines[i].split("=", 1)[0].strip().lower() == key.lower():
            lines[i] = f"{key}={value}"
            return
    lines.insert(end, f"{key}={value}")


def build_optiscaler_ini(base_text: str | None, preset: str, enabler: bool) -> str:
    lines = (base_text if base_text and base_text.strip() else MINIMAL_INI_HEADER).replace("\r\n", "\n").split("\n")
    for section, key, value in (
        ("Upscalers", "Dx12Upscaler", "ffx"),
        ("DlssNr", "Enabled", "true"), ("DlssNr", "RunBeforeSR", "true"), ("DlssNr", "Passes", "1"),
        ("DlssNr", "LocalTone", "0"), ("DlssNr", "LocalStructure", "1"), ("DlssNr", "SkinStructure", "1"), ("DlssNr", "ApplyAfterRR", "false"),
        ("Log", "LogToFile", "true"), ("Log", "LogLevel", "2"),
    ):
        _ini_set(lines, section, key, value)
    if preset == "performance":
        for section, key, value in (
            ("UpscaleRatio", "UpscaleRatioOverrideEnabled", "true"), ("UpscaleRatio", "UpscaleRatioOverrideValue", "3.0"),
            ("FrameGen", "Enabled", "true"), ("FrameGen", "FGInput", "nvngxfg"), ("FrameGen", "FGNvngxReplacement", "combo" if enabler else "ffx"),
            ("DLSSG", "InterpolationCount", "2"),
        ):
            _ini_set(lines, section, key, value)
    return "\n".join(lines).rstrip("\n") + "\n"


def scan_tree(root: Path, max_depth: int = 4) -> tuple[list[str], list[str]]:
    fsr: list[str] = []
    anti_cheat: list[str] = []
    base_depth = len(root.parts)
    for current, dirs, files in os.walk(root):
        current_path = Path(current)
        depth = len(current_path.parts) - base_depth
        if depth >= max_depth:
            dirs[:] = []
        lower_files = {name.lower(): name for name in files}
        for marker in FSR_MARKERS:
            if marker in lower_files:
                fsr.append(str((current_path / lower_files[marker]).relative_to(root)))
        names = [name.lower() for name in dirs] + list(lower_files)
        for name in names:
            stem = Path(name).stem.lower()
            if any(marker in stem for marker in ANTI_CHEAT_MARKERS):
                rel = current_path.relative_to(root)
                anti_cheat.append(str(rel / name))
    return sorted(set(fsr)), sorted(set(anti_cheat))


def binary_mentions_dx12(path: Path) -> bool:
    try:
        with path.open("rb") as stream:
            overlap = b""
            while True:
                chunk = stream.read(4 * 1024 * 1024)
                if not chunk:
                    return False
                data = (overlap + chunk).lower()
                if b"d3d12.dll" in data or b"d3d12core.dll" in data:
                    return True
                overlap = data[-32:]
    except OSError:
        return False


def dx12_evidence(exe: Path, fsr_markers: list[str]) -> list[str]:
    evidence: list[str] = []
    if binary_mentions_dx12(exe):
        evidence.append(exe.name)
    for relative in fsr_markers:
        if "dx12" in relative.lower():
            evidence.append(relative)

    # Engines often load their renderer dynamically, so inspect a bounded set
    # of nearby x64 DLLs before treating the graphics API as unknown.
    try:
        candidates = sorted(exe.parent.glob("*.dll"), key=lambda path: path.stat().st_size, reverse=True)
    except OSError:
        candidates = []
    checked = 0
    for candidate in candidates:
        if checked >= 24:
            break
        if pe_machine(candidate) != 0x8664:
            continue
        checked += 1
        if binary_mentions_dx12(candidate):
            evidence.append(candidate.name)
    return sorted(set(evidence))


def fetch_latest_release() -> dict[str, Any]:
    request = urllib.request.Request(UPSTREAM_API, headers={"User-Agent": "DLSS5-AMD-Swapper direct-game helper"})
    with urllib.request.urlopen(request, timeout=15) as response:
        data = json.load(response)
    for asset in data.get("assets", []):
        if asset.get("name") == UPSTREAM_ASSET:
            digest = asset.get("digest")
            if not isinstance(digest, str) or not digest.startswith("sha256:"):
                raise RuntimeError("GitHub did not provide a SHA-256 digest for the official setup asset")
            return {
                "tag": data.get("tag_name"),
                "asset": asset["name"],
                "size": int(asset["size"]),
                "sha256": digest[7:].lower(),
                "release_page": UPSTREAM_RELEASES,
            }
    raise RuntimeError("The latest official release has no dlssnr_on_amd_setup.exe asset")


def validate_setup(path: Path) -> dict[str, Any]:
    if not path.is_file():
        raise RuntimeError(f"Setup executable not found: {path}")
    release = fetch_latest_release()
    actual = sha256(path)
    size = path.stat().st_size
    if actual != release["sha256"] or size != release["size"]:
        raise RuntimeError(
            "The supplied setup file does not match the latest official GitHub release digest. "
            f"Download it from {UPSTREAM_RELEASES}."
        )
    return release


def validate_nr_dll(path: Path) -> dict[str, Any]:
    if not path.is_file():
        raise RuntimeError(f"DLSS-NR DLL not found: {path}")
    if path.name.lower() != "nvngx_dlssnr.dll":
        raise RuntimeError("The model/runtime file must be named nvngx_dlssnr.dll")
    if pe_machine(path) != 0x8664:
        raise RuntimeError("nvngx_dlssnr.dll is not a 64-bit Windows PE file")
    return {"sha256": sha256(path), "size": path.stat().st_size}


def check_game(exe: Path) -> dict[str, Any]:
    exe = exe.resolve()
    if not exe.is_file() or exe.suffix.lower() != ".exe":
        raise RuntimeError("--game must point to the game's executable")
    machine = pe_machine(exe)
    fsr, anti_cheat = scan_tree(exe.parent)
    dx12 = dx12_evidence(exe, fsr)
    return {
        "game_exe": exe.name,
        "x64": machine == 0x8664,
        "pe_machine": f"0x{machine:04x}" if machine is not None else None,
        "dx12_evidence": dx12,
        "fsr_markers": fsr,
        "anti_cheat_markers": anti_cheat,
        "route": "amd-fsr-direct",
        "eligible": machine == 0x8664 and bool(fsr) and bool(dx12) and not anti_cheat,
    }


def managed_paths(folder: Path) -> dict[str, Path]:
    names = list(KNOWN_PROXY_NAMES) + list(KNOWN_RUNTIME_FILES) + [UPSTREAM_ASSET, "nvngx_dlssnr.dll"]
    return {name: folder / name for name in names}


def find_manifest(folder: Path) -> Path | None:
    current = folder / MANIFEST_NAME
    if current.is_file():
        return current
    legacy = folder / LEGACY_MANIFEST_NAME
    if legacy.is_file():
        return legacy
    return None


def read_runtime_config(path: Path) -> dict[str, str]:
    """Read the small upstream INI without exposing paths or comments."""
    if not path.is_file():
        return {}
    values: dict[str, str] = {}
    active_section = ""
    for raw_line in path.read_text(encoding="utf-8", errors="replace").splitlines():
        line = raw_line.strip()
        if not line or line.startswith((";", "#")):
            continue
        if line.startswith("[") and line.endswith("]"):
            active_section = line[1:-1].strip().lower()
            continue
        if active_section != "dlssnronamd" or "=" not in line:
            continue
        key, value = line.split("=", 1)
        values[key.strip()] = value.strip()
    return values


def verify_rich_runtime_config(path: Path) -> dict[str, int]:
    values = read_runtime_config(path)
    required = {
        "Enabled": 1,
        "UseFsrInputs": 1,
        "UseDepth": 1,
        "Temporal": 1,
        "Interop": 1,
        "Inline": 1,
    }
    missing: list[str] = []
    verified: dict[str, int] = {}
    for key, expected in required.items():
        try:
            actual = int(values.get(key, ""), 0)
        except ValueError:
            actual = -1
        if actual != expected:
            missing.append(f"{key}={expected}")
        else:
            verified[key] = actual
    if missing:
        raise RuntimeError(
            "Upstream setup did not enable the required direct-game Neural Rendering path: "
            + ", ".join(missing)
        )
    return verified


def snapshot(folder: Path) -> dict[str, Any]:
    return {name: state for name, path in managed_paths(folder).items() if (state := file_state(path)) is not None}


def run_setup(setup: Path, folder: Path, update: bool) -> subprocess.CompletedProcess[str]:
    # The upstream setup is console driven. These inputs accept the selected
    # folder and its detected/default proxy name. Existing managed installs use
    # the documented U action. Any unexpected confirmation defaults to No/quit.
    responses = "y\n"
    if update:
        responses += "u\n"
    responses += "\n\n\n"
    return subprocess.run(
        [str(setup)],
        cwd=str(folder),
        input=responses,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        timeout=120,
        check=False,
    )


def install(args: argparse.Namespace) -> dict[str, Any]:
    game = Path(args.game).resolve()
    info = check_game(game)
    if not info["x64"]:
        raise RuntimeError("The first direct-game route supports 64-bit games only")
    if info["anti_cheat_markers"]:
        raise RuntimeError("Anti-cheat markers were found. Direct-game installation is blocked for this target")
    if not info["fsr_markers"] and not args.force:
        raise RuntimeError("No FSR runtime marker was found near the game executable")
    if not info["dx12_evidence"] and not args.force:
        raise RuntimeError("No DirectX 12 evidence was found near the game executable")

    setup_source = Path(args.upstream_setup).resolve()
    nr_source = Path(args.nr_dll).resolve()
    release = validate_setup(setup_source)
    nr_meta = validate_nr_dll(nr_source)
    folder = game.parent
    manifest_path = folder / MANIFEST_NAME
    existing_manifest_path = find_manifest(folder)
    if existing_manifest_path is not None and manifest_route(folder) == ROUTE_OPTISCALER:
        raise RuntimeError("This game is managed by the OptiScaler pre-SR route. Run --remove first")
    if args.update and existing_manifest_path is None:
        raise RuntimeError("--update requires an existing managed direct-game manifest")
    if existing_manifest_path is not None and not args.update:
        raise RuntimeError(f"A managed install already exists: {existing_manifest_path}. Use --update")

    before = snapshot(folder)
    if not args.update:
        existing_proxy = [name for name in KNOWN_PROXY_NAMES if name in before]
        if existing_proxy:
            raise RuntimeError(
                "A proxy DLL already exists in the game folder and is not managed by this helper: "
                + ", ".join(existing_proxy)
            )

    previous_manifest = existing_manifest_path.read_bytes() if existing_manifest_path is not None else None
    local_setup = folder / UPSTREAM_ASSET
    if local_setup.exists() and sha256(local_setup) != release["sha256"]:
        raise RuntimeError(f"A different {UPSTREAM_ASSET} already exists in the game folder")
    local_nr = folder / "nvngx_dlssnr.dll"
    if local_nr.exists() and sha256(local_nr) != nr_meta["sha256"]:
        raise RuntimeError("A different nvngx_dlssnr.dll already exists in the game folder")

    with tempfile.TemporaryDirectory(prefix="dlss5-amd-swapper-direct-backup-") as temp_name:
        backup = Path(temp_name)
        for name in before:
            source = folder / name
            if source.is_file():
                shutil.copy2(source, backup / name)
        try:
            if not local_setup.exists():
                shutil.copy2(setup_source, local_setup)
            if not local_nr.exists():
                shutil.copy2(nr_source, local_nr)

            completed = run_setup(local_setup, folder, update=args.update)
            after = snapshot(folder)
            changed_proxy = [name for name in KNOWN_PROXY_NAMES if name in after and before.get(name) != after.get(name)]
            if args.update:
                old_manifest = json.loads(previous_manifest.decode("utf-8")) if previous_manifest else {}
                installed_proxy = [name for name in (old_manifest.get("installed_proxy_names") or []) if name in after]
                if not installed_proxy:
                    installed_proxy = [name for name in KNOWN_PROXY_NAMES if name in after]
            else:
                installed_proxy = changed_proxy
            has_config = "dlssnr_on_amd.ini" in after
            has_weights = "dlssnr_on_amd_weights.bin" in after
            if completed.returncode != 0 or not has_config or not has_weights or not installed_proxy:
                tail = completed.stdout[-2000:] if completed.stdout else ""
                raise RuntimeError(
                    f"Upstream setup did not produce a verifiable install (exit {completed.returncode}).\n{tail}"
                )

            verified_config = verify_rich_runtime_config(folder / "dlssnr_on_amd.ini")

            manifest = {
                "schema_version": 1,
                "created_unix": int(time.time()),
                "route": "amd-fsr-direct",
                "game_exe": game.name,
                "upstream": release,
                "nvngx_dlssnr": nr_meta,
                "compatibility": info,
                "verified_config": verified_config,
                "before": before,
                "after": after,
                "installed_proxy_names": installed_proxy,
                "managed_setup_was_created": UPSTREAM_ASSET not in before,
                "model_was_copied": "nvngx_dlssnr.dll" not in before,
            }
            manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
            if existing_manifest_path is not None and existing_manifest_path != manifest_path and existing_manifest_path.exists():
                existing_manifest_path.unlink()
            return manifest
        except Exception:
            # Restore the exact pre-install state for every file this helper or
            # the upstream setup is allowed to manage. This also covers failed
            # updates, where existing proxy/config/weights must not be lost.
            for name, path in managed_paths(folder).items():
                if name in before:
                    backup_path = backup / name
                    if backup_path.is_file():
                        shutil.copy2(backup_path, path)
                elif path.exists():
                    path.unlink()
            if previous_manifest is not None and existing_manifest_path is not None:
                existing_manifest_path.write_bytes(previous_manifest)
                if existing_manifest_path != manifest_path and manifest_path.exists():
                    manifest_path.unlink()
            elif manifest_path.exists():
                manifest_path.unlink()
            raise


def remove(args: argparse.Namespace) -> dict[str, Any]:
    game = Path(args.game).resolve()
    folder = game.parent
    manifest_path = find_manifest(folder)
    if manifest_path is None:
        raise RuntimeError("No managed direct-game manifest was found")
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    after = manifest.get("after") or {}
    removed: list[str] = []
    preserved: list[str] = []

    for name in list(KNOWN_PROXY_NAMES) + ["dlssnr_on_amd.ini", "dlssnr_on_amd_weights.bin", "dlssnr_on_amd.log"]:
        path = folder / name
        expected = after.get(name)
        before = (manifest.get("before") or {}).get(name)
        if before is not None:
            preserved.append(name)
            continue
        if not path.exists():
            continue
        current = file_state(path)
        if expected is not None and current == expected:
            path.unlink()
            removed.append(name)
        else:
            preserved.append(name)

    if manifest.get("managed_setup_was_created"):
        path = folder / UPSTREAM_ASSET
        expected = after.get(UPSTREAM_ASSET)
        if path.exists() and expected is not None and file_state(path) == expected:
            path.unlink()
            removed.append(UPSTREAM_ASSET)

    if args.remove_model and manifest.get("model_was_copied"):
        path = folder / "nvngx_dlssnr.dll"
        expected = after.get("nvngx_dlssnr.dll")
        if path.exists() and expected is not None and file_state(path) == expected:
            path.unlink()
            removed.append("nvngx_dlssnr.dll")
        elif path.exists():
            preserved.append("nvngx_dlssnr.dll")

    created_but_remaining = []
    before = manifest.get("before") or {}
    for name in list(KNOWN_PROXY_NAMES) + list(KNOWN_RUNTIME_FILES):
        if name not in before and (folder / name).exists():
            created_but_remaining.append(name)

    manifest_retained = bool(created_but_remaining)
    if not manifest_retained:
        manifest_path.unlink()
    return {
        "removed": sorted(removed),
        "preserved": sorted(set(preserved)),
        "model_removed": args.remove_model,
        "manifest_retained": manifest_retained,
        "remaining_managed_files": sorted(created_but_remaining),
    }


def _read_manifest(folder: Path) -> tuple[Path | None, dict[str, Any] | None]:
    path = find_manifest(folder)
    if path is None:
        return None, None
    try:
        return path, json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError:
        return path, None


def manifest_route(folder: Path) -> str | None:
    path, manifest = _read_manifest(folder)
    if path is None or manifest is None:
        return None
    return manifest.get("route") or ROUTE_POST_FSR


def install_optiscaler(args: argparse.Namespace, version_reader=None) -> dict[str, Any]:
    game = Path(args.game).resolve()
    info = check_game(game)
    if not info["x64"]:
        raise RuntimeError("The pre-SR route supports 64-bit games only")
    if info["anti_cheat_markers"]:
        raise RuntimeError("Anti-cheat markers were found. Direct-game installation is blocked for this target")
    if not info["fsr_markers"] and not args.force:
        raise RuntimeError("No FSR runtime marker was found near the game executable")
    if not info["dx12_evidence"] and not args.force:
        raise RuntimeError("No DirectX 12 evidence was found near the game executable")
    if not args.package or not args.weights:
        raise RuntimeError("--route optiscaler-presr requires --package and --weights")
    package = validate_package(Path(args.package), version_reader)
    weights = Path(args.weights).resolve()
    if not is_real_weights(weights):
        raise RuntimeError("--weights must point to a generated dlssnr_on_amd_weights.bin")
    folder = game.parent
    package_upscaler = Path(package["dependency_folder"]) / OPTI_REQUIRED_UPSCALER if package["dependency_folder"] else None
    if not (package_upscaler and package_upscaler.is_file()) and not (folder / OPTI_REQUIRED_UPSCALER).is_file():
        raise RuntimeError(f"{OPTI_REQUIRED_UPSCALER} is missing from both the package and the game folder")

    route = manifest_route(folder)
    previous_manifest: dict[str, Any] | None = None
    if route == ROUTE_POST_FSR:
        raise RuntimeError("This game has the post-FSR route installed. Run --remove first, then install the pre-SR route")
    if route == ROUTE_OPTISCALER and not args.update:
        raise RuntimeError("This game already has the pre-SR route. Use --update")
    if route == ROUTE_OPTISCALER:
        previous_manifest = _read_manifest(folder)[1]
    if route is None and args.update:
        raise RuntimeError("--update requires an existing managed pre-SR manifest")

    proxy_name = args.proxy_name or (previous_manifest.get("proxy_name") if previous_manifest else None) or "dxgi.dll"
    if proxy_name.lower() not in OPTI_PROXY_NAMES:
        raise RuntimeError("Unsupported --proxy-name")
    proxy_name = proxy_name.lower()

    dependency_relatives: list[str] = []
    if package["dependency_folder"]:
        deps = Path(package["dependency_folder"])
        dependency_relatives = [str(Path(OPTI_DEPENDENCY_FOLDER) / path.relative_to(deps)) for path in sorted(deps.rglob("*")) if path.is_file()]
    enabler_relative = str(Path(OPTI_DEPENDENCY_FOLDER) / OPTI_ENABLER)
    previous_after_keys = list((previous_manifest.get("after") or {}).keys()) if previous_manifest else []
    managed = list(dict.fromkeys([*OPTI_PROXY_NAMES, *OPTI_ROOT_MANAGED, *dependency_relatives, enabler_relative, *previous_after_keys]))

    def snapshot() -> dict[str, Any]:
        return {name: state for name in managed if (state := file_state(folder / name)) is not None}

    before = snapshot()
    if not args.update:
        unmanaged = [name for name in OPTI_PROXY_NAMES if name in before]
        if unmanaged:
            raise RuntimeError("A proxy DLL already exists in the game folder and is not managed by this helper: " + ", ".join(unmanaged))
    elif previous_manifest and proxy_name.lower() not in [p.lower() for p in (previous_manifest.get("installed_proxy_names") or [])]:
        raise RuntimeError("Update must keep the proxy name recorded in the manifest")

    manifest_path = folder / MANIFEST_NAME
    previous_bytes = manifest_path.read_bytes() if manifest_path.is_file() else None

    with tempfile.TemporaryDirectory(prefix="dlss5-amd-swapper-presr-backup-") as temp_name:
        backup = Path(temp_name)
        for name in before:
            target = backup / name
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(folder / name, target)
        written: list[str] = []
        preexisting: list[str] = []
        original_before = (previous_manifest.get("before") or before) if previous_manifest else before
        try:
            def copy_verified(source: Path, relative: str) -> None:
                destination = folder / relative
                destination.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(source, destination)
                if sha256(source) != sha256(destination):
                    raise RuntimeError(f"Copy verification failed for {relative}")
                written.append(relative)

            copy_verified(Path(package["optiscaler_dll"]), proxy_name)
            for name in OPTI_PASS_NAMES:
                match = next((p for p in package["pass_dlls"] if Path(p).name.lower() == name), None)
                source = match if match is not None else package["pass_dlls"][0]
                copy_verified(Path(source), name)
            copy_verified(weights, OPTI_WEIGHTS)
            for relative in dependency_relatives:
                source = Path(package["root"]) / relative
                if relative in before and before[relative]["sha256"] == sha256(source):
                    if relative in original_before:
                        preexisting.append(relative)
                    continue
                copy_verified(source, relative)
            enabler_available = package["enabler"] is not None
            if args.preset == "performance" and enabler_available:
                copy_verified(Path(package["enabler"]), enabler_relative)
            base_text = Path(package["ini"]).read_text(encoding="utf-8", errors="replace") if package["ini"] else None
            (folder / "OptiScaler.ini").write_text(build_optiscaler_ini(base_text, args.preset, enabler_available), encoding="utf-8")
            written.append("OptiScaler.ini")

            after = snapshot()
            manifest = {
                "schema_version": 3,
                "created_unix": int(time.time()),
                "route": ROUTE_OPTISCALER,
                "game_exe": game.name,
                "proxy_name": proxy_name,
                "preset": args.preset,
                "package": {
                    "root": package["root"], "layout": package["layout"], "fork_version": package["fork_version"],
                    "sha256sums_verified": package["sha256sums_verified"], "files": package["files"],
                },
                "weights": {"source": str(weights), **file_state(weights)},
                "compatibility": {"x64": info["x64"], "fsr_markers": info["fsr_markers"], "dx12_evidence": info["dx12_evidence"], "anti_cheat_markers": info["anti_cheat_markers"]},
                "previous_route": previous_manifest.get("previous_route") if previous_manifest else None,
                "before": original_before,
                "after": after,
                "preexisting_dependencies": sorted(set(((previous_manifest.get("preexisting_dependencies") or []) if previous_manifest else [])) | set(preexisting)),
                "installed_proxy_names": [proxy_name],
            }
            manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
            legacy = folder / LEGACY_MANIFEST_NAME
            if legacy.is_file():
                legacy.unlink()
            return manifest
        except Exception:
            for name in managed:
                target = folder / name
                if name in before:
                    source = backup / name
                    if source.is_file():
                        shutil.copy2(source, target)
                elif target.is_file():
                    target.unlink()
            deps_dir = folder / OPTI_DEPENDENCY_FOLDER
            if deps_dir.is_dir() and not any(path.is_file() for path in deps_dir.rglob("*")):
                shutil.rmtree(deps_dir, ignore_errors=True)
            if previous_bytes is not None:
                manifest_path.write_bytes(previous_bytes)
            elif manifest_path.exists():
                manifest_path.unlink()
            raise


def remove_optiscaler(args: argparse.Namespace) -> dict[str, Any]:
    folder = Path(args.game).resolve().parent
    manifest_path, manifest = _read_manifest(folder)
    if manifest_path is None or manifest is None or manifest.get("route") != ROUTE_OPTISCALER:
        raise RuntimeError("No managed pre-SR manifest was found")
    before = manifest.get("before") or {}
    after = manifest.get("after") or {}
    preexisting = set(manifest.get("preexisting_dependencies") or [])
    removed: list[str] = []
    preserved: list[str] = []
    for name in dict.fromkeys([*after.keys(), *(manifest.get("installed_proxy_names") or [])]):
        path = folder / name
        if name in before or name in preexisting:
            preserved.append(name)
            continue
        if not path.is_file():
            continue
        if after.get(name) == file_state(path):
            path.unlink()
            removed.append(name)
        else:
            preserved.append(name)
    deps_dir = folder / OPTI_DEPENDENCY_FOLDER
    if deps_dir.is_dir() and not any(path.is_file() for path in deps_dir.rglob("*")):
        shutil.rmtree(deps_dir, ignore_errors=True)
    remaining = sorted(name for name in after if name not in before and name not in preexisting and (folder / name).is_file())
    if not remaining:
        manifest_path.unlink()
    return {"removed": sorted(removed), "preserved": sorted(set(preserved)), "manifest_retained": bool(remaining), "remaining_managed_files": remaining}


def summarize_presr(folder: Path) -> dict[str, Any]:
    def read(name: str) -> tuple[str, str | None, int]:
        path = folder / name
        if not path.is_file():
            return "", None, 0
        raw = path.read_bytes()
        return raw[-1024 * 1024:].decode("utf-8", errors="replace"), hashlib.sha256(raw).hexdigest(), len(raw)

    presr, presr_hash, presr_bytes = read("amd_presr.log")
    opti, opti_hash, opti_bytes = read("OptiScaler.log")
    completed = [int(value) for value in re.findall(r"Completed AMD pre-SR passes=(\d+)", presr)]
    running = re.findall(r"DLSS-NR running [^:]*: target (\d+x\d+), model (\d+x\d+)", opti)
    costs = [(float(total), float(model)) for total, model in re.findall(r"DLSS-NR cost: ([\d.]+) ms total = ([\d.]+) ms model", opti)]
    fault = re.compile(r"(AMD pre-SR: (?!idle)|HIP completion timeout|Unsupported AMD pre-SR|hash mismatch|weights\.bin is required|LoadLibrary failed|initialization failed|Cannot load amdhip64_7\.dll|AMD stopped|AMD timeout)")
    faults = [line.strip() for line in presr.splitlines() if fault.search(line.strip())]
    adapter = re.search(r"HIP adapter:\s*(.+)", presr)
    return {
        "pre_sr_active": bool(completed) or ("DLSS-NR running" in opti),
        "hip_adapter": adapter.group(1).strip() if adapter else None,
        "passes_initialized": len(re.findall(r"Initialized independent AMD pass \d+", presr)),
        "passes_completed": completed[-1] if completed else None,
        "target_size": running[-1][0] if running else None,
        "model_size": running[-1][1] if running else None,
        "mean_total_ms": sum(cost[0] for cost in costs) / len(costs) if costs else None,
        "mean_model_ms": sum(cost[1] for cost in costs) / len(costs) if costs else None,
        "cost_samples": len(costs),
        "last_fault": faults[-1] if faults else None,
        "presr_log_sha256": presr_hash, "presr_log_bytes": presr_bytes,
        "optiscaler_log_sha256": opti_hash, "optiscaler_log_bytes": opti_bytes,
    }


def summarize_runtime_log(path: Path) -> dict[str, Any] | None:
    if not path.is_file():
        return None
    raw = path.read_bytes()
    text = raw.decode("utf-8", errors="replace")
    job_rows = [
        (int(job), int(wall), float(gpu), float(wait), history.lower() == "on", zero_copy.lower() == "zero-copy")
        for job, wall, gpu, wait, history, zero_copy in re.findall(
            r"network job (\d+) done in (\d+) ms \(([\d.]+) ms network on the GPU, "
            r"([\d.]+) ms waiting for the capture; history (on|off), (zero-copy|copied)\)",
            text,
            flags=re.IGNORECASE,
        )
    ]
    jobs = [wall for _, wall, _, _, _, _ in job_rows]
    gpu_jobs = [gpu for _, _, gpu, _, _, _ in job_rows]
    wait_jobs = [wait for _, _, _, wait, _, _ in job_rows]
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
    result: dict[str, Any] = {
        "sha256": hashlib.sha256(raw).hexdigest(),
        "bytes": len(raw),
        "fidelityfx_dispatch_detected": bool(re.search(r"(?im)^first ffxDispatch type ", text)),
        "fidelityfx_upscaler_hooks": len(re.findall(r"(?im)^hooked amd_fidelityfx_.*!ffxDispatch", text)),
        "fault_lines": len(re.findall(r"(?im)^FAULT:", text)),
        "gpu_error_lines": len(re.findall(r"(?im)^job \d+ GPU errors:", text)),
        "timed_job_samples": len(jobs),
    }
    if staging:
        result["fsr_inputs"] = {
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
            result["processing_resolution"] = {
                "swapchain_size": [output_w, output_h],
                "fsr_input_size": [input_w, input_h],
                "input_to_output_width_ratio": input_w / output_w if output_w else None,
                "input_to_output_height_ratio": input_h / output_h if output_h else None,
                "full_output_resolution_input": input_w == output_w and input_h == output_h,
            }
            if input_w == output_w and input_h == output_h:
                result["performance_hint"] = (
                    "FSR input equals output resolution. Select an in-game FSR quality/upscaling mode "
                    "to keep native output while running Neural Rendering on a smaller render-resolution input."
                )
    if interop:
        result["interop"] = {
            "inputs": interop[1].lower(),
            "output": interop[2].lower(),
            "mode": interop[3].strip(),
        }
    if hip:
        result["hip_runtime"] = {
            "device_count": int(hip[1]),
            "driver": hip[2],
            "runtime": hip[3],
        }
    if jobs:
        ordered = sorted(jobs)
        result["job_wall_ms"] = {
            "mean": sum(jobs) / len(jobs),
            "median": ordered[len(ordered) // 2],
            "minimum": ordered[0],
            "maximum": ordered[-1],
        }
    if gpu_jobs:
        result["network_gpu_ms"] = {
            "mean": sum(gpu_jobs) / len(gpu_jobs),
            "minimum": min(gpu_jobs),
            "maximum": max(gpu_jobs),
        }
        result["capture_wait_ms"] = {
            "mean": sum(wait_jobs) / len(wait_jobs),
            "minimum": min(wait_jobs),
            "maximum": max(wait_jobs),
        }
        result["history_enabled_samples"] = sum(1 for _, _, _, _, history, _ in job_rows if history)
        result["zero_copy_samples"] = sum(1 for _, _, _, _, _, zero_copy in job_rows if zero_copy)
    return result


def diagnose(args: argparse.Namespace) -> dict[str, Any]:
    game = Path(args.game).resolve()
    folder = game.parent
    manifest_path = find_manifest(folder)
    manifest = json.loads(manifest_path.read_text(encoding="utf-8")) if manifest_path is not None else None
    config_path = folder / "dlssnr_on_amd.ini"
    config_values = read_runtime_config(config_path)
    safe_config_keys = ("Enabled", "UseFsrInputs", "UseDepth", "Temporal", "Interop", "Inline")
    safe_config = {key: config_values.get(key) for key in safe_config_keys if key in config_values}
    runtime_log = summarize_runtime_log(folder / "dlssnr_on_amd.log")
    rich_runtime = bool(
        runtime_log
        and runtime_log.get("fidelityfx_dispatch_detected")
        and runtime_log.get("fsr_inputs")
        and runtime_log.get("timed_job_samples", 0) > 0
    )
    return {
        "schema_version": 1,
        "game": check_game(game),
        "managed_install": manifest is not None,
        "manifest_upstream": manifest.get("upstream") if manifest else None,
        "runtime_config": safe_config or None,
        "rich_runtime_path_observed": rich_runtime,
        "runtime_log": runtime_log,
    }


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--game", required=True, help="Path to the game's .exe")
    action = parser.add_mutually_exclusive_group(required=True)
    action.add_argument("--check", action="store_true")
    action.add_argument("--install", action="store_true")
    action.add_argument("--update", action="store_true")
    action.add_argument("--remove", action="store_true")
    action.add_argument("--diagnose", action="store_true")
    parser.add_argument("--route", choices=("post-fsr", "optiscaler-presr"), default="post-fsr")
    parser.add_argument("--upstream-setup", help="User-downloaded official dlssnr_on_amd_setup.exe")
    parser.add_argument("--nr-dll", help="User-supplied nvngx_dlssnr.dll")
    parser.add_argument("--package", help="User-supplied OptiScaler AMD pre-SR package folder")
    parser.add_argument("--weights", help="Locally generated dlssnr_on_amd_weights.bin")
    parser.add_argument("--preset", choices=("quality", "performance"), default="quality")
    parser.add_argument("--proxy-name", default=None)
    parser.add_argument("--passes", type=int, choices=(1, 2, 3))
    parser.add_argument("--force", action="store_true", help="Override uncertain FSR/DX12 detection; anti-cheat remains blocked")
    parser.add_argument("--remove-model", action="store_true", help="Also remove a model DLL copied by this helper")
    parser.add_argument("--output", type=Path, help="Write JSON result/diagnostic to this path")
    return parser.parse_args(argv)


def main() -> int:
    args = parse_args()
    try:
        folder = Path(args.game).resolve().parent
        route = manifest_route(folder)
        if args.install or args.update:
            if args.route == "optiscaler-presr":
                result = install_optiscaler(args)
            else:
                if not args.upstream_setup or not args.nr_dll:
                    raise RuntimeError("--install/--update require --upstream-setup and --nr-dll")
                result = install(args)
        elif args.remove:
            result = remove_optiscaler(args) if route == ROUTE_OPTISCALER else remove(args)
        elif args.diagnose:
            result = diagnose(args)
            if route == ROUTE_OPTISCALER:
                result["route"] = ROUTE_OPTISCALER
                result["pre_sr"] = summarize_presr(folder)
        else:
            result = check_game(Path(args.game))
        encoded = json.dumps(result, indent=2) + "\n"
        if args.output:
            args.output.parent.mkdir(parents=True, exist_ok=True)
            args.output.write_text(encoded, encoding="utf-8")
        print(encoded, end="")
        return 0
    except (OSError, RuntimeError, ValueError, json.JSONDecodeError, subprocess.TimeoutExpired) as error:
        print(f"direct-game: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())

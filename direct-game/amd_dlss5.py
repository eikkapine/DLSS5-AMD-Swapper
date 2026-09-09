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
                installed_proxy = [name for name in old_manifest.get("installed_proxy_names", []) if name in after]
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
    after = manifest.get("after", {})
    removed: list[str] = []
    preserved: list[str] = []

    for name in list(KNOWN_PROXY_NAMES) + ["dlssnr_on_amd.ini", "dlssnr_on_amd_weights.bin", "dlssnr_on_amd.log"]:
        path = folder / name
        expected = after.get(name)
        before = manifest.get("before", {}).get(name)
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
    before = manifest.get("before", {})
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


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--game", required=True, help="Path to the game's .exe")
    action = parser.add_mutually_exclusive_group(required=True)
    action.add_argument("--check", action="store_true")
    action.add_argument("--install", action="store_true")
    action.add_argument("--update", action="store_true")
    action.add_argument("--remove", action="store_true")
    action.add_argument("--diagnose", action="store_true")
    parser.add_argument("--upstream-setup", help="User-downloaded official dlssnr_on_amd_setup.exe")
    parser.add_argument("--nr-dll", help="User-supplied nvngx_dlssnr.dll")
    parser.add_argument("--force", action="store_true", help="Override uncertain FSR/DX12 detection; anti-cheat remains blocked")
    parser.add_argument("--remove-model", action="store_true", help="Also remove a model DLL copied by this helper")
    parser.add_argument("--output", type=Path, help="Write JSON result/diagnostic to this path")
    args = parser.parse_args()

    try:
        if args.install or args.update:
            if not args.upstream_setup or not args.nr_dll:
                raise RuntimeError("--install/--update require --upstream-setup and --nr-dll")
            result = install(args)
        elif args.remove:
            result = remove(args)
        elif args.diagnose:
            result = diagnose(args)
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

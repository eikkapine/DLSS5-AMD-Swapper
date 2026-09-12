import json
import os
import shutil
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import amd_dlss5 as helper  # noqa: E402


def fake_fork(_path):
    return ("OptiScaler", "10.0.0-dev (amd-presr-multipass-local) (20260907_075847)")


def write_pe(path: Path, marker: bytes = b"") -> Path:
    shutil.copy(sys.executable, path)
    if marker:
        with path.open("ab") as stream:
            stream.write(marker)
    return path


WEIGHTS_OID = "6bf8dc931ef3ccffe18c82de26ab374156e7f19539ffcf8eabaa25dca5cf15ab"


def write_sums(root: Path, weights_hash: str = WEIGHTS_OID, extra: str = "") -> None:
    lines = []
    for relative in ("OptiScaler.dll", "dlssnr_amd_pass1.dll", "OptiScaler.ini", "dlssnr_on_amd_weights.bin"):
        digest = weights_hash if relative == "dlssnr_on_amd_weights.bin" else helper.sha256(root / relative)
        lines.append(f"{digest} *{relative}")
    (root / "SHA256SUMS.txt").write_text("\n".join(lines) + "\n" + extra, encoding="utf-8")


def make_package(parent: Path) -> Path:
    root = parent / "OptiScaler-AMD-PreSR-Multipass-v1.2"
    (root / "OptiScaler").mkdir(parents=True)
    write_pe(root / "OptiScaler.dll")
    for index in (1, 2, 3):
        write_pe(root / f"dlssnr_amd_pass{index}.dll", b"dlssnr_amd")
    (root / "OptiScaler.ini").write_text("[Upscalers]\nDx12Upscaler=auto\n\n[DlssNr]\nEnabled=auto\n", encoding="utf-8")
    (root / "dlssnr_on_amd_weights.bin").write_text(f"version https://git-lfs.github.com/spec/v1\noid sha256:{WEIGHTS_OID}\nsize 147689451\n", encoding="utf-8")
    (root / "OptiScaler" / "amd_fidelityfx_upscaler_dx12.dll").write_bytes(b"\x01\x02")
    return root


class Fixture:
    def __init__(self):
        self.temp = Path(tempfile.mkdtemp(prefix="dlss5-presr-"))
        self.package = make_package(self.temp)
        self.game_dir = self.temp / "Game"
        self.game_dir.mkdir()
        self.exe = write_pe(self.game_dir / "FixtureGame.exe", b"d3d12.dll")
        (self.game_dir / "amd_fidelityfx_upscaler_dx12.dll").write_bytes(b"\x01")
        self.weights = self.temp / "weights.bin"
        self.weights.write_bytes(b"\x00" * (1024 * 1024 + 3))

    def cleanup(self):
        shutil.rmtree(self.temp, ignore_errors=True)


class PackageTests(unittest.TestCase):
    def test_validate_package(self):
        f = Fixture()
        self.addCleanup(f.cleanup)
        info = helper.validate_package(f.package, version_reader=fake_fork)
        self.assertEqual(info["layout"], "package")
        self.assertEqual(len(info["pass_dlls"]), 3)
        self.assertIsNone(info["weights"])
        self.assertIn("amd-presr", info["fork_version"])
        with self.assertRaises(RuntimeError):
            helper.validate_package(f.package, version_reader=lambda _p: ("OptiScaler", "10.0.0-dev (792f2f1)"))

    def test_sha256sums_accepts_matching_lfs_pointer(self):
        f = Fixture()
        self.addCleanup(f.cleanup)
        write_sums(f.package)
        info = helper.validate_package(f.package, version_reader=fake_fork)
        self.assertTrue(info["sha256sums_verified"])
        self.assertEqual(info["sha256sums_warnings"], [])
        self.assertIsNone(info["weights"])

    def test_sha256sums_rejects_mismatched_lfs_pointer(self):
        f = Fixture()
        self.addCleanup(f.cleanup)
        write_sums(f.package, weights_hash="0" * 64)
        with self.assertRaises(RuntimeError) as raised:
            helper.validate_package(f.package, version_reader=fake_fork)
        self.assertIn("dlssnr_on_amd_weights.bin", str(raised.exception))

    def test_sha256sums_stale_doc_checksum_is_a_warning(self):
        f = Fixture()
        self.addCleanup(f.cleanup)
        (f.package / "LEIA-ME-AMD.md").write_text("edited after the checksums were generated\n", encoding="utf-8")
        write_sums(f.package, extra="0" * 64 + " *LEIA-ME-AMD.md\n")
        info = helper.validate_package(f.package, version_reader=fake_fork)
        self.assertTrue(info["sha256sums_verified"])
        self.assertEqual(info["sha256sums_warnings"], ["LEIA-ME-AMD.md"])
        (f.package / "OptiScaler.ini").write_text("tampered\n", encoding="utf-8")
        with self.assertRaises(RuntimeError):
            helper.validate_package(f.package, version_reader=fake_fork)

    def test_weights_detection(self):
        f = Fixture()
        self.addCleanup(f.cleanup)
        self.assertFalse(helper.is_real_weights(f.package / "dlssnr_on_amd_weights.bin"))
        self.assertTrue(helper.is_real_weights(f.weights))
        found = helper.find_local_weights(None, [f.temp], None)
        self.assertIsNone(found)
        shutil.copy(f.weights, f.game_dir / "dlssnr_on_amd_weights.bin")
        found = helper.find_local_weights(None, [f.game_dir], None)
        self.assertEqual(found["size"], f.weights.stat().st_size)

    def test_ini_presets(self):
        quality = helper.build_optiscaler_ini("[DlssNr]\nEnabled=auto\n", "quality", False)
        self.assertIn("RunBeforeSR=true", quality)
        self.assertIn("Dx12Upscaler=ffx", quality)
        self.assertNotIn("FGInput", quality)
        performance = helper.build_optiscaler_ini(None, "performance", True)
        self.assertIn("UpscaleRatioOverrideValue=3.0", performance)
        self.assertIn("FGNvngxReplacement=combo", performance)
        self.assertIn("InterpolationCount=2", performance)
        self.assertIn("FGNvngxReplacement=ffx", helper.build_optiscaler_ini(None, "performance", False))

    def test_ini_passes(self):
        ini = helper.build_optiscaler_ini(None, "quality", False, passes=3)
        self.assertIn("Passes=3", ini)
        with self.assertRaises(ValueError):
            helper.build_optiscaler_ini(None, "quality", False, passes=4)
        with self.assertRaises(ValueError):
            helper.build_optiscaler_ini(None, "quality", False, passes=0)


class InstallTests(unittest.TestCase):
    def test_crimson_desert_blocks_only_known_bad_presr_proxy(self):
        f = Fixture()
        self.addCleanup(f.cleanup)
        crimson = f.game_dir / "CrimsonDesert.exe"
        f.exe.replace(crimson)
        f.exe = crimson
        bad_hash = helper.sha256(f.package / "OptiScaler.dll")
        original = helper.CRIMSON_DESERT_INCOMPATIBLE_PROXY_SHA256
        helper.CRIMSON_DESERT_INCOMPATIBLE_PROXY_SHA256 = bad_hash
        try:
            args = helper.parse_args(["--game", str(f.exe), "--install", "--route", "optiscaler-presr", "--package", str(f.package), "--weights", str(f.weights)])
            with self.assertRaisesRegex(RuntimeError, "incompatible with Crimson Desert startup"):
                helper.install_optiscaler(args, version_reader=fake_fork)
            self.assertFalse((f.game_dir / "dxgi.dll").exists())
            self.assertFalse((f.game_dir / ".dlss5-amd-swapper.json").exists())
        finally:
            helper.CRIMSON_DESERT_INCOMPATIBLE_PROXY_SHA256 = original

    def test_install_and_remove(self):
        f = Fixture()
        self.addCleanup(f.cleanup)
        self.assertEqual(helper.check_game(f.exe)["eligible_routes"], ["amd-fsr-direct", "amd-optiscaler-presr"])
        args = helper.parse_args(["--game", str(f.exe), "--install", "--route", "optiscaler-presr", "--package", str(f.package), "--weights", str(f.weights), "--passes", "2"])
        manifest = helper.install_optiscaler(args, version_reader=fake_fork)
        self.assertEqual(manifest["route"], "amd-optiscaler-presr")
        self.assertEqual(manifest["schema_version"], 3)
        for name in ("dxgi.dll", "OptiScaler.ini", "dlssnr_amd_pass3.dll", "dlssnr_on_amd_weights.bin", os.path.join("OptiScaler", "amd_fidelityfx_upscaler_dx12.dll")):
            self.assertTrue((f.game_dir / name).exists(), name)
        self.assertIn("Passes=2", (f.game_dir / "OptiScaler.ini").read_text(encoding="utf-8"))
        on_disk = json.loads((f.game_dir / ".dlss5-amd-swapper.json").read_text(encoding="utf-8"))
        self.assertEqual(on_disk["installed_proxy_names"], ["dxgi.dll"])
        self.assertIn("preexisting_dependencies", on_disk)
        with self.assertRaises(RuntimeError):
            helper.install_optiscaler(args, version_reader=fake_fork)

        removed = helper.remove_optiscaler(helper.parse_args(["--game", str(f.exe), "--remove"]))
        self.assertIn("dxgi.dll", removed["removed"])
        self.assertFalse((f.game_dir / "dlssnr_amd_pass1.dll").exists())
        self.assertFalse((f.game_dir / ".dlss5-amd-swapper.json").exists())

    def test_install_rolls_back(self):
        f = Fixture()
        self.addCleanup(f.cleanup)
        (f.game_dir / "OptiScaler.ini").mkdir()
        args = helper.parse_args(["--game", str(f.exe), "--install", "--route", "optiscaler-presr", "--package", str(f.package), "--weights", str(f.weights)])
        with self.assertRaises(Exception):
            helper.install_optiscaler(args, version_reader=fake_fork)
        self.assertFalse((f.game_dir / "dxgi.dll").exists())
        self.assertFalse((f.game_dir / "dlssnr_on_amd_weights.bin").exists())
        self.assertFalse((f.game_dir / ".dlss5-amd-swapper.json").exists())

    def test_diagnose_presr(self):
        f = Fixture()
        self.addCleanup(f.cleanup)
        (f.game_dir / "amd_presr.log").write_text("HIP adapter: AMD Radeon RX 9070 XT\nInitialized independent AMD pass 1\nCompleted AMD pre-SR passes=1\n", encoding="utf-8")
        (f.game_dir / "OptiScaler.log").write_text("DlssNr_Dx12::Dispatch DLSS-NR running before SR: target 3840x2160, model 1280x720, guides 1280x720 (preset 0)\nDLSS-NR cost: 12.00 ms total = 10.00 ms model + 2.00 ms ours (16% ours)\n", encoding="utf-8")
        summary = helper.summarize_presr(f.game_dir)
        self.assertTrue(summary["pre_sr_active"])
        self.assertEqual(summary["model_size"], "1280x720")
        self.assertEqual(summary["passes_completed"], 1)
        self.assertAlmostEqual(summary["mean_total_ms"], 12.0)
        self.assertEqual(len(summary["presr_log_sha256"]), 64)

    def test_diagnose_presr_tolerates_prefixed_lines(self):
        f = Fixture()
        self.addCleanup(f.cleanup)
        (f.game_dir / "amd_presr.log").write_text(
            "[12:00:01] HIP adapter: AMD Radeon RX 9070 XT\r\n[12:00:02] AMD engine initialization failed\r\n",
            encoding="utf-8",
        )
        (f.game_dir / "OptiScaler.log").write_text(
            "[info] DlssNr_Dx12::Dispatch DLSS-NR running before SR (no sizes)\n",
            encoding="utf-8",
        )
        summary = helper.summarize_presr(f.game_dir)
        self.assertTrue(summary["pre_sr_active"])
        self.assertEqual(summary["hip_adapter"], "AMD Radeon RX 9070 XT")
        self.assertIsNotNone(summary["last_fault"])
        self.assertTrue(summary["last_fault"].endswith("AMD engine initialization failed"))
        self.assertIsNone(summary["model_size"])

    def test_post_fsr_diagnostics_use_latest_game_session(self):
        f = Fixture()
        self.addCleanup(f.cleanup)
        log = f.game_dir / "dlssnr_on_amd.log"
        log.write_text(
            "dlssnr_amd v0.2.17 (build old) loaded into FixtureGame.exe as winmm.dll\n"
            "engine init ok\n"
            "first ffxDispatch type TEST\n"
            "staging ready: colour 640x480 dxgi 28 test; motion 640x480 dxgi 16; depth 640x480 dxgi 40 (inverted 1); exposure yes; residual on\n"
            "network job 1 done in 8 ms (6.25 ms network on the GPU, 0.50 ms waiting for the capture; history on, zero-copy)\n"
            "dlssnr_amd v0.2.18 (build new) loaded into FixtureGame.exe as winmm.dll\n"
            "hooked ID3D12CommandQueue::ExecuteCommandLists\n",
            encoding="utf-8",
        )
        summary = helper.summarize_runtime_log(log, f.exe.name)
        self.assertTrue(summary["session_scoped"])
        self.assertEqual(summary["runtime_version"], "v0.2.18")
        self.assertFalse(summary["engine_initialized"])
        self.assertFalse(summary["fidelityfx_dispatch_detected"])
        self.assertEqual(summary["timed_job_samples"], 0)

    def test_summarize_runtime_log_reports_hook_failures(self):
        f = Fixture()
        self.addCleanup(f.cleanup)
        log = f.game_dir / "dlssnr_on_amd.log"
        fixture = (
            "dlssnr_amd v0.2.17 (build 976a3fa0) loaded into SecretGame.exe as winmm.dll from X:\\Games\\SecretGame\\bin64\\; log X:\\Games\\SecretGame\\bin64\\dlssnr_on_amd.log; settings X:\\Games\\SecretGame\\bin64\\dlssnr_on_amd.ini\n"
            "detour of ID3D12CommandQueue::ExecuteCommandLists failed (5)\n"
            "hooked IDXGIFactory2::CreateSwapChainForHwnd\n"
            "detour of IDXGIFactory::CreateSwapChain failed (5)\n"
            "detour of IDXGISwapChain::Present failed (5)\n"
            "hooked IDXGISwapChain1::Present1\n"
            "dlssnr_amd v0.2.17 (build 976a3fa0) loaded into crashpad_handler.exe as winmm.dll from X:\\Games\\SecretGame\\bin64\\; log X:\\Games\\SecretGame\\bin64\\dlssnr_on_amd.log; settings X:\\Games\\SecretGame\\bin64\\dlssnr_on_amd.ini\n"
            "hooked ID3D12CommandQueue::ExecuteCommandLists\n"
            "hooked IDXGIFactory2::CreateSwapChainForHwnd\n"
            "hooked IDXGIFactory::CreateSwapChain\n"
            "hooked IDXGISwapChain::Present\n"
            "hooked IDXGISwapChain1::Present1\n"
            "swapchain 0000000071031300 created on queue 0000000070A75760 (device 0000000070776F40)\n"
            "swapchain 000000010F3E1170 created on queue 00000000DD119140 (device 0000000070776F40)\n"
        )
        log.write_text(fixture, encoding="utf-8")
        summary = helper.summarize_runtime_log(log, "SecretGame.exe")
        self.assertIsNotNone(summary)
        self.assertEqual(summary["hook_failures"], 3)
        self.assertEqual(summary["swapchains_created"], 2)
        self.assertEqual(summary["hooks_installed"], 2)
        self.assertTrue(summary["hooks_failed"])
        self.assertFalse(summary["engine_initialized"])
        self.assertTrue(summary["session_scoped"])

        older_success = (
            "dlssnr_amd v0.2.16 (build old) loaded into SecretGame.exe as winmm.dll\n"
            "engine init ok\n"
            "first ffxDispatch type TEST\n"
            "staging ready: colour 640x480 dxgi 28 test; motion 640x480 dxgi 16; depth 640x480 dxgi 40 (inverted 1); exposure yes; residual on\n"
            "network job 1 done in 8 ms (6.25 ms network on the GPU, 0.50 ms waiting for the capture; history on, zero-copy)\n"
        )
        log.write_text(older_success + fixture, encoding="utf-8")
        summary_with_older = helper.summarize_runtime_log(log, "SecretGame.exe")
        self.assertIsNotNone(summary_with_older)
        self.assertEqual(summary_with_older["hook_failures"], 3)
        self.assertEqual(summary_with_older["swapchains_created"], 2)
        self.assertEqual(summary_with_older["hooks_installed"], 2)
        self.assertTrue(summary_with_older["hooks_failed"])
        self.assertFalse(summary_with_older["engine_initialized"])
        self.assertTrue(summary_with_older["session_scoped"])

    def test_log_without_session_header_rejects_rich_verdict(self):
        f = Fixture()
        self.addCleanup(f.cleanup)
        log = f.game_dir / "dlssnr_on_amd.log"
        startup_and_jobs = (
            "first ffxDispatch type TEST\n"
            "staging ready: colour 640x480 dxgi 28 test; motion 640x480 dxgi 16; depth 640x480 dxgi 40 (inverted 1); exposure yes; residual on\n"
            "env: swapchain 1920x1080 format 28,\n"
            "network job 1 done in 8 ms (6.25 ms network on the GPU, 0.50 ms waiting for the capture; history on, zero-copy)\n"
        )
        log.write_text(startup_and_jobs, encoding="utf-8")
        summary = helper.summarize_runtime_log(log, "SecretGame.exe")
        self.assertIsNotNone(summary)
        self.assertFalse(summary["session_scoped"])
        self.assertIsNone(summary["runtime_version"])

        args = helper.parse_args(["--game", str(f.exe), "--diagnose"])
        diag = helper.diagnose(args)
        self.assertFalse(diag["rich_runtime_path_observed"])
        self.assertIsNotNone(diag["runtime_log"])
        self.assertFalse(diag["runtime_log"]["session_scoped"])

        log.write_text(
            f"dlssnr_amd v0.2.17 (build test) loaded into {f.exe.name} as winmm.dll\n" + startup_and_jobs,
            encoding="utf-8",
        )
        diag_rich = helper.diagnose(args)
        self.assertTrue(diag_rich["rich_runtime_path_observed"])
        self.assertTrue(diag_rich["runtime_log"]["session_scoped"])

    def test_update_keeps_manifest_proxy_name(self):
        f = Fixture()
        self.addCleanup(f.cleanup)
        install_args = helper.parse_args([
            "--game", str(f.exe),
            "--install",
            "--route", "optiscaler-presr",
            "--package", str(f.package),
            "--weights", str(f.weights),
            "--proxy-name", "winmm.dll",
        ])
        manifest = helper.install_optiscaler(install_args, version_reader=fake_fork)
        self.assertEqual(manifest["proxy_name"], "winmm.dll")
        self.assertTrue((f.game_dir / "winmm.dll").exists())
        self.assertFalse((f.game_dir / "dxgi.dll").exists())

        update_args = helper.parse_args([
            "--game", str(f.exe),
            "--update",
            "--route", "optiscaler-presr",
            "--package", str(f.package),
            "--weights", str(f.weights),
            "--preset", "performance",
        ])
        updated_manifest = helper.install_optiscaler(update_args, version_reader=fake_fork)
        self.assertTrue(updated_manifest)
        self.assertTrue((f.game_dir / "winmm.dll").exists())
        self.assertFalse((f.game_dir / "dxgi.dll").exists())
        on_disk = json.loads((f.game_dir / ".dlss5-amd-swapper.json").read_text(encoding="utf-8"))
        self.assertEqual(on_disk["proxy_name"], "winmm.dll")
        self.assertEqual(updated_manifest["proxy_name"], "winmm.dll")


if __name__ == "__main__":
    unittest.main()

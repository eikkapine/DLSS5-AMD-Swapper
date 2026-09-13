import json
import os
import shutil
import sys
import tempfile
import unittest
from unittest.mock import patch
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
        light = helper.build_optiscaler_ini("[DlssNr]\nEnabled=auto\n", "light", False)
        self.assertIn("RunBeforeSR=true", light)
        self.assertIn("Dx12Upscaler=ffx", light)
        self.assertIn("LogToFile=true", light)
        self.assertIn("UpscaleRatioOverrideValue=1.5", light)
        # A preset must never switch frame generation on by itself.
        for name in ("light", "balanced", "detail", "max"):
            ini = helper.build_optiscaler_ini(None, name, True)
            self.assertNotIn("FGInput", ini)
            self.assertNotIn("InterpolationCount", ini)
        detail = helper.build_optiscaler_ini(None, "detail", False)
        self.assertIn("Passes=2", detail)
        self.assertIn("LocalStructure=2.0", detail)
        self.assertIn("UpscaleRatioOverrideValue=2.0", detail)
        # Legacy manifests recorded only quality/performance; performance forced a 3.0 ratio.
        legacy = helper.build_optiscaler_ini(None, "performance", True)
        self.assertIn("Passes=3", legacy)
        self.assertIn("UpscaleRatioOverrideValue=3.0", legacy)
        self.assertEqual(helper.normalize_preset("quality"), "balanced")
        # Explicit scaling beats the preset tier; game controlled writes no forced ratio at all.
        self.assertIn("UpscaleRatioOverrideValue=1.0", helper.build_optiscaler_ini(None, "detail", False, scaling="dlaa"))
        game_controlled = helper.build_optiscaler_ini(None, "detail", False, scaling="gamecontrolled")
        self.assertIn("UpscaleRatioOverrideEnabled=false", game_controlled)
        self.assertNotIn("UpscaleRatioOverrideValue", game_controlled)

    def test_crimson_ini_disables_dxgi_spoofing_only_for_crimson(self):
        base = "[Spoofing]\nDxgi=true\n"
        self.assertIn("Dxgi=false", helper.build_optiscaler_ini(base, "quality", False, game_exe="CrimsonDesert.exe"))
        self.assertIn("Dxgi=true", helper.build_optiscaler_ini(base, "quality", False, game_exe="OtherGame.exe"))

    def test_ini_passes(self):
        ini = helper.build_optiscaler_ini(None, "quality", False, passes=3)
        self.assertIn("Passes=3", ini)
        with self.assertRaises(ValueError):
            helper.build_optiscaler_ini(None, "quality", False, passes=4)
        with self.assertRaises(ValueError):
            helper.build_optiscaler_ini(None, "quality", False, passes=0)


class RuntimeConfigTests(unittest.TestCase):
    common = "[DlssNrOnAmd]\nEnabled=1\nUseFsrInputs=1\nUseDepth=1\nTemporal=1\nInterop=1\n"
    modern = "Async=0\nPreUpscale=1\nPreHistory=0\nInlineWaitMs=200\n"

    def test_legacy_contract_still_requires_inline(self):
        f = Fixture()
        self.addCleanup(f.cleanup)
        config = f.game_dir / "dlssnr_on_amd.ini"
        config.write_text(self.common + "Inline=1\n", encoding="utf-8")
        self.assertEqual(helper.verify_rich_runtime_config(config, "v0.2.17")["Inline"], 1)
        config.write_text(self.common + self.modern, encoding="utf-8")
        for tag in (None, "v0.2.17", "unknown"):
            with self.subTest(tag=tag), self.assertRaisesRegex(RuntimeError, "Inline=1"):
                helper.verify_rich_runtime_config(config, tag)

    def test_v030_contract_accepts_new_setup_without_inline(self):
        f = Fixture()
        self.addCleanup(f.cleanup)
        config = f.game_dir / "dlssnr_on_amd.ini"
        config.write_text(self.common + self.modern.replace("InlineWaitMs=200", "InlineWaitMs=50"), encoding="utf-8")
        verified = helper.verify_rich_runtime_config(config, "v0.3.0")
        self.assertEqual(verified["Async"], 0)
        self.assertEqual(verified["PreUpscale"], 1)
        self.assertEqual(verified["PreHistory"], 0)
        self.assertNotIn("Inline", verified)
        self.assertNotIn("InlineWaitMs", verified)
        diagnostic = helper.diagnose(helper.parse_args(["--game", str(f.exe), "--diagnose"]))
        self.assertEqual(diagnostic["runtime_config"]["Async"], "0")
        self.assertEqual(diagnostic["runtime_config"]["PreUpscale"], "1")
        self.assertEqual(diagnostic["runtime_config"]["InlineWaitMs"], "50")

    def test_v030_contract_rejects_missing_or_disabled_required_modes(self):
        f = Fixture()
        self.addCleanup(f.cleanup)
        config = f.game_dir / "dlssnr_on_amd.ini"
        valid = self.common + self.modern + "Inline=1\n"
        for key, expected in (("UseFsrInputs", 1), ("UseDepth", 1), ("Temporal", 1), ("Interop", 1), ("Enabled", 1), ("Async", 0), ("PreUpscale", 1), ("PreHistory", 0)):
            for replacement in ("", f"{key}={1 - expected}\n"):
                with self.subTest(key=key, replacement=replacement):
                    config.write_text(valid.replace(f"{key}={expected}\n", replacement), encoding="utf-8")
                    with self.assertRaisesRegex(RuntimeError, f"{key}={expected}"):
                        helper.verify_rich_runtime_config(config, "v0.3.0")

    def test_v030_install_records_new_contract_and_rolls_back_invalid_output(self):
        for valid in (True, False):
            with self.subTest(valid=valid):
                f = Fixture()
                self.addCleanup(f.cleanup)
                setup = write_pe(f.temp / helper.UPSTREAM_ASSET)
                model = write_pe(f.temp / "nvngx_dlssnr.dll")
                release = {"tag": "v0.3.0", "sha256": helper.sha256(setup), "size": setup.stat().st_size}

                def run_setup(local_setup, folder, update, expected_sha256):
                    self.assertFalse(update)
                    self.assertEqual(expected_sha256, release["sha256"])
                    (folder / "winmm.dll").write_bytes(b"fixture proxy")
                    shutil.copyfile(f.weights, folder / "dlssnr_on_amd_weights.bin")
                    modern = self.modern if valid else self.modern.replace("Async=0", "Async=1")
                    (folder / "dlssnr_on_amd.ini").write_text(self.common + modern, encoding="utf-8")
                    return helper.subprocess.CompletedProcess([str(local_setup)], 0, stdout="Fixture setup complete")

                args = helper.parse_args(["--game", str(f.exe), "--install", "--upstream-setup", str(setup), "--nr-dll", str(model)])
                with patch.object(helper, "validate_setup", return_value=release), patch.object(helper, "run_setup", side_effect=run_setup):
                    if valid:
                        manifest = helper.install(args)
                        self.assertEqual(manifest["verified_config"]["Async"], 0)
                        self.assertNotIn("Inline", manifest["verified_config"])
                        self.assertTrue((f.game_dir / helper.MANIFEST_NAME).exists())
                    else:
                        with self.assertRaisesRegex(RuntimeError, "Async=0"):
                            helper.install(args)
                        for name in ("winmm.dll", "dlssnr_on_amd.ini", "dlssnr_on_amd_weights.bin", helper.MANIFEST_NAME, helper.UPSTREAM_ASSET, "nvngx_dlssnr.dll"):
                            self.assertFalse((f.game_dir / name).exists(), name)
                        self.assertTrue(f.exe.exists())


class SetupRunnerTests(unittest.TestCase):
    def test_setup_runs_isolated_with_explicit_target_and_correct_responses(self):
        f = Fixture()
        self.addCleanup(f.cleanup)
        folder = f.game_dir.rename(f.temp / "Game with spaces")
        setup = write_pe(folder / helper.UPSTREAM_ASSET)
        proxy = folder / "dxgi.dll"
        proxy.write_bytes(b"managed proxy must stay unchanged")
        expected_hash = helper.sha256(setup)
        staged_folders = []
        for update in (False, True):
            with self.subTest(update=update):
                def execute(command, **kwargs):
                    staged_exe = Path(command[0])
                    staged_folders.append(staged_exe.parent)
                    self.assertNotEqual(staged_exe.parent, folder)
                    self.assertEqual(Path(kwargs["cwd"]), staged_exe.parent)
                    self.assertEqual(command, [str(staged_exe), str(folder.resolve())])
                    self.assertEqual(helper.sha256(staged_exe), expected_hash)
                    self.assertEqual({p.name for p in staged_exe.parent.iterdir()}, {helper.UPSTREAM_ASSET, "SpecialK.deny.dlssnr_on_amd_setup"})
                    self.assertEqual(kwargs["input"], "u\n\n\n\n" if update else "\n\n\n")
                    self.assertEqual(kwargs["timeout"], 120)
                    self.assertFalse(kwargs["check"])
                    self.assertNotIn("shell", kwargs)
                    if os.name == "nt":
                        self.assertEqual(kwargs["creationflags"], helper.subprocess.CREATE_NO_WINDOW)
                    return helper.subprocess.CompletedProcess(command, 0, stdout="fixture finished")

                with patch.object(helper.subprocess, "run", side_effect=execute):
                    result = helper.run_setup(setup, folder, update, expected_hash)
                self.assertEqual(result.returncode, 0)
                self.assertFalse(staged_folders[-1].exists())
                self.assertEqual(helper.sha256(setup), expected_hash)
                self.assertEqual(proxy.read_bytes(), b"managed proxy must stay unchanged")

    def test_setup_cleans_isolated_copy_after_process_failure(self):
        f = Fixture()
        self.addCleanup(f.cleanup)
        setup = write_pe(f.temp / helper.UPSTREAM_ASSET)
        staged_folders = []
        for failure in (OSError("fixture start failure"), helper.subprocess.TimeoutExpired("fixture", 120)):
            with self.subTest(failure=type(failure).__name__):
                def execute(command, **kwargs):
                    staged_folders.append(Path(command[0]).parent)
                    raise failure

                with patch.object(helper.subprocess, "run", side_effect=execute), self.assertRaises(type(failure)):
                    helper.run_setup(setup, f.game_dir, True, helper.sha256(setup))
                self.assertFalse(staged_folders[-1].exists())
                self.assertTrue(setup.exists())

    def test_setup_rejects_changed_isolated_copy_before_execution(self):
        f = Fixture()
        self.addCleanup(f.cleanup)
        setup = write_pe(f.temp / helper.UPSTREAM_ASSET)
        expected_hash = helper.sha256(setup)
        staged_folders = []

        def corrupt_copy(source, destination):
            staged_folders.append(Path(destination).parent)
            Path(destination).write_bytes(b"fixture changed during copy")

        with patch.object(helper.shutil, "copy2", side_effect=corrupt_copy), patch.object(helper.subprocess, "run") as execute:
            with self.assertRaisesRegex(RuntimeError, "SHA-256 verification"):
                helper.run_setup(setup, f.game_dir, False, expected_hash)
            execute.assert_not_called()
        self.assertFalse(staged_folders[-1].exists())
        self.assertEqual(helper.sha256(setup), expected_hash)


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

    def test_overlay_keys_written_and_player_rebind_preserved(self):
        text = helper.build_optiscaler_ini(None, "quality", False)
        self.assertIn("OverlayMenu=true", text)
        self.assertIn("ShortcutKey=0x2E", text)
        self.assertIn("FpsOverlayType=2", text)
        self.assertIn("FpsShortcutKey=0x21", text)
        # The overlay saves its settings back to this file, so a rebound key must survive.
        kept = helper.build_optiscaler_ini("[Menu]\nShortcutKey=0x08\n", "quality", False)
        self.assertIn("ShortcutKey=0x08", kept)
        self.assertNotIn("ShortcutKey=0x2E", kept)
        rally = helper.build_optiscaler_ini(None, "quality", False, game_exe="acr.exe")
        self.assertIn("ManualInputPolling=true", rally)
        self.assertNotIn("ManualInputPolling", helper.build_optiscaler_ini(None, "quality", False, game_exe="Other.exe"))

    def test_game_shipped_microsoft_dll_is_not_a_competing_loader(self):
        f = Fixture()
        self.addCleanup(f.cleanup)
        # Cyberpunk 2077 ships Microsoft's dbghelp.dll in bin\x64; it is not a proxy loader.
        system = Path(os.environ.get("SystemRoot", r"C:\Windows")) / "System32" / "dbghelp.dll"
        if not system.is_file():
            self.skipTest("Microsoft dbghelp.dll is unavailable on this machine")
        shutil.copy2(system, f.game_dir / "dbghelp.dll")
        (f.game_dir / "winmm.dll").write_bytes(b"\x01")
        self.assertTrue(helper.is_microsoft_system_dll(f.game_dir / "dbghelp.dll"))
        self.assertFalse(helper.is_microsoft_system_dll(f.game_dir / "winmm.dll"))
        self.assertEqual(
            helper.conflicting_proxy_names(f.game_dir, {"dbghelp.dll", "winmm.dll"}),
            ["winmm.dll"],
        )

    def test_diagnose_presr_reports_when_no_upscaler_ran(self):
        f = Fixture()
        self.addCleanup(f.cleanup)
        # Real Assetto Corsa Rally session: loader healthy, but the game never created an
        # upscaler context, so amd_presr.log is never written at all.
        loaded = "[04:24:17.219662] [W] OptiScaler v10.0.0-dev (amd-presr) loaded\n"
        (f.game_dir / "OptiScaler.log").write_text(loaded + "Quirk: Disable FSR 3.0 Inputs\n", encoding="utf-8")
        summary = helper.summarize_presr(f.game_dir)
        self.assertFalse(summary["upscaler_observed"])
        self.assertFalse(summary["pre_sr_active"])
        self.assertTrue(summary["loader_observed"])

        (f.game_dir / "OptiScaler.log").write_text(loaded + "ffxCreateContext_Dx12 context created: 1EE8\n", encoding="utf-8")
        self.assertTrue(helper.summarize_presr(f.game_dir)["upscaler_observed"])

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
        self.assertFalse(summary["pre_sr_active"])
        self.assertEqual(summary["hip_adapter"], "AMD Radeon RX 9070 XT")
        self.assertIsNotNone(summary["last_fault"])
        self.assertTrue(summary["last_fault"].endswith("AMD engine initialization failed"))
        self.assertIsNone(summary["model_size"])

    def test_diagnose_presr_running_marker_requires_completed_work(self):
        f = Fixture()
        self.addCleanup(f.cleanup)
        (f.game_dir / "OptiScaler.log").write_text("DLSS-NR running before SR: target 1920x1080, model 1280x720\n", encoding="utf-8")
        self.assertFalse(helper.summarize_presr(f.game_dir)["pre_sr_active"])

    def test_diagnose_presr_fault_invalidates_previous_completed_work(self):
        f = Fixture()
        self.addCleanup(f.cleanup)
        presr = f.game_dir / "amd_presr.log"
        presr.write_text("Completed AMD pre-SR passes=1\nAMD engine initialization failed\n", encoding="utf-8")
        (f.game_dir / "OptiScaler.log").write_text("DLSS-NR cost: 12.00 ms total = 10.00 ms model\n", encoding="utf-8")
        failed = helper.summarize_presr(f.game_dir)
        self.assertFalse(failed["pre_sr_active"])
        self.assertIsNone(failed["passes_completed"])
        with presr.open("a", encoding="utf-8") as stream:
            stream.write("Completed AMD pre-SR passes=1\n")
        self.assertTrue(helper.summarize_presr(f.game_dir)["pre_sr_active"])

    def test_diagnose_presr_optiscaler_fault_invalidates_costs(self):
        f = Fixture()
        self.addCleanup(f.cleanup)
        opti = f.game_dir / "OptiScaler.log"
        opti.write_text("DLSS-NR cost: 12.00 ms total = 10.00 ms model\nAMD engine initialization failed\n", encoding="utf-8")
        failed = helper.summarize_presr(f.game_dir)
        self.assertFalse(failed["pre_sr_active"])
        self.assertEqual(failed["cost_samples"], 0)
        with opti.open("a", encoding="utf-8") as stream:
            stream.write("DLSS-NR cost: 11.00 ms total = 9.00 ms model\n")
        recovered = helper.summarize_presr(f.game_dir)
        self.assertTrue(recovered["pre_sr_active"])
        self.assertEqual(recovered["mean_total_ms"], 11.0)

    def test_diagnose_reports_unresolved_fault_before_recovered_fault(self):
        f = Fixture()
        self.addCleanup(f.cleanup)
        (f.game_dir / "amd_presr.log").write_text("HIP completion timeout\nCompleted AMD pre-SR passes=1\n", encoding="utf-8")
        (f.game_dir / "OptiScaler.log").write_text("OptiScaler v10.0.0-dev loaded\nDLSS-NR initialization failed\n", encoding="utf-8")
        summary = helper.summarize_presr(f.game_dir)
        self.assertFalse(summary["pre_sr_active"])
        self.assertEqual(summary["last_fault"], "DLSS-NR initialization failed")

    def test_diagnose_tolerates_log_disappearing_before_open(self):
        f = Fixture()
        self.addCleanup(f.cleanup)
        (f.game_dir / "amd_presr.log").write_text("Completed AMD pre-SR passes=1\n", encoding="utf-8")
        original_open = Path.open

        def disappearing_open(path, *args, **kwargs):
            if path.name == "amd_presr.log":
                raise FileNotFoundError("log rotated")
            return original_open(path, *args, **kwargs)

        with patch.object(Path, "open", new=disappearing_open):
            summary = helper.summarize_presr(f.game_dir)
        self.assertFalse(summary["pre_sr_active"])
        self.assertEqual(summary["presr_log_bytes"], 0)
        self.assertIsNone(summary["presr_log_sha256"])

    def test_diagnose_requires_positive_finite_neural_timings(self):
        f = Fixture()
        self.addCleanup(f.cleanup)
        for total, model in (("0.00", "0.00"), ("1.00", "0.00"), ("9" * 400, "1.00")):
            with self.subTest(total=total, model=model):
                (f.game_dir / "OptiScaler.log").write_text(f"DLSS-NR cost: {total} ms total = {model} ms model\n", encoding="utf-8")
                summary = helper.summarize_presr(f.game_dir)
                self.assertFalse(summary["pre_sr_active"])
                self.assertEqual(summary["cost_samples"], 0)

    def test_assetto_guidance_explains_supported_inputs(self):
        f = Fixture()
        self.addCleanup(f.cleanup)
        f.exe = f.exe.rename(f.game_dir / "acr.exe")
        guidance = helper.check_game(f.exe)["pre_sr_activation_guidance"]
        self.assertIn("DLSS or XeSS", guidance)
        self.assertIn("FSR inputs", guidance)
        self.assertIn(helper.ASSETTO_RALLY_GUIDE, guidance)

    def test_diagnose_presr_uses_latest_optiscaler_session(self):
        f = Fixture()
        self.addCleanup(f.cleanup)
        (f.game_dir / "amd_presr.log").write_text("Completed AMD pre-SR passes=1\n", encoding="utf-8")
        opti = f.game_dir / "OptiScaler.log"
        opti.write_text(
            "[00:01:20.591504] [W] OptiScaler v10.0.0-dev (old) loaded\n"
            "DLSS-NR cost: 12.00 ms total = 10.00 ms model\n"
            "[00:04:20.591504] [W] OptiScaler v10.0.0-dev (new) loaded\n"
            "DLSS-NR running before SR: target 1920x1080, model 1280x720\n",
            encoding="utf-8",
        )
        latest = helper.summarize_presr(f.game_dir)
        self.assertTrue(latest["session_scoped"])
        self.assertFalse(latest["pre_sr_active"])
        self.assertEqual(latest["cost_samples"], 0)
        with opti.open("a", encoding="utf-8") as stream:
            stream.write("DLSS-NR cost: 11.00 ms total = 9.00 ms model\n")
        self.assertTrue(helper.summarize_presr(f.game_dir)["pre_sr_active"])

    def test_diagnose_presr_distinguishes_old_logs_and_missing_payload(self):
        f = Fixture()
        self.addCleanup(f.cleanup)
        install_args = helper.parse_args(["--game", str(f.exe), "--install", "--route", "optiscaler-presr", "--package", str(f.package), "--weights", str(f.weights)])
        manifest = helper.install_optiscaler(install_args, version_reader=fake_fork)
        presr = f.game_dir / "amd_presr.log"
        presr.write_text("Completed AMD pre-SR passes=1\n", encoding="utf-8")
        old_time = manifest["created_unix"] - 60
        os.utime(presr, (old_time, old_time))
        args = helper.parse_args(["--game", str(f.exe), "--diagnose"])
        stale = helper.diagnose(args)["pre_sr"]
        self.assertEqual(stale["installation_status"], "installed")
        self.assertFalse(stale["pre_sr_active"])
        self.assertEqual(stale["stale_logs"], ["amd_presr.log"])
        self.assertEqual(len(stale["presr_log_sha256"]), 64)
        os.utime(presr, (manifest["created_unix"] + 1, manifest["created_unix"] + 1))
        self.assertTrue(helper.diagnose(args)["pre_sr"]["pre_sr_active"])
        (f.game_dir / "dxgi.dll").unlink()
        missing = helper.diagnose(args)["pre_sr"]
        self.assertEqual(missing["installation_status"], "incomplete")
        self.assertFalse(missing["pre_sr_active"])
        self.assertTrue(missing["runtime_work_observed"])
        self.assertIn("dxgi.dll", missing["missing_install_files"])

    def test_diagnose_leftover_presr_log_does_not_hide_missing_install(self):
        f = Fixture()
        self.addCleanup(f.cleanup)
        (f.game_dir / "amd_presr.log").write_text("Completed AMD pre-SR passes=1\n", encoding="utf-8")
        diagnostic = helper.diagnose(helper.parse_args(["--game", str(f.exe), "--diagnose"]))
        self.assertFalse(diagnostic["managed_install"])
        self.assertFalse(diagnostic["pre_sr"]["pre_sr_active"])
        self.assertIn("dxgi.dll", diagnostic["pre_sr"]["missing_install_files"])

    def test_diagnose_presr_waiting_and_zero_passes_are_not_success_or_fault(self):
        f = Fixture()
        self.addCleanup(f.cleanup)
        (f.game_dir / "amd_presr.log").write_text("AMD pre-SR: waiting for a DirectX 12 SR frame\nCompleted AMD pre-SR passes=0\n", encoding="utf-8")
        summary = helper.summarize_presr(f.game_dir)
        self.assertFalse(summary["pre_sr_active"])
        self.assertIsNone(summary["last_fault"])

    def test_diagnose_presr_large_log_uses_tail_for_evidence_and_full_hash(self):
        f = Fixture()
        self.addCleanup(f.cleanup)
        path = f.game_dir / "amd_presr.log"
        path.write_text("Completed AMD pre-SR passes=1\n" + "x" * (2 * 1024 * 1024) + "\nAMD engine initialization failed\n", encoding="utf-8")
        summary = helper.summarize_presr(f.game_dir)
        self.assertFalse(summary["pre_sr_active"])
        self.assertEqual(summary["presr_log_sha256"], helper.sha256(path))
        self.assertEqual(summary["presr_log_bytes"], path.stat().st_size)

    def test_diagnose_presr_retains_session_boundary_outside_tail(self):
        f = Fixture()
        self.addCleanup(f.cleanup)
        (f.game_dir / "amd_presr.log").write_text("Completed AMD pre-SR passes=1\n", encoding="utf-8")
        (f.game_dir / "OptiScaler.log").write_text("OptiScaler v10.0.0-dev loaded\n" + "x" * (2 * 1024 * 1024) + "\nDisable FSR 3.0 Inputs\n", encoding="utf-8")
        summary = helper.summarize_presr(f.game_dir)
        self.assertTrue(summary["session_scoped"])
        self.assertTrue(summary["loader_observed"])
        self.assertTrue(summary["fsr_inputs_disabled"])
        self.assertFalse(summary["pre_sr_active"])

    def test_diagnose_correlates_real_pass_ticks_without_cost_logs(self):
        f = Fixture()
        self.addCleanup(f.cleanup)
        presr = f.game_dir / "amd_presr.log"
        opti = f.game_dir / "OptiScaler.log"
        presr.write_text("25000 HIP runtime: fixture HIP runtime\n30000 Completed AMD pre-SR passes=1 at 320x180\n", encoding="utf-8")
        opti.write_text("[00:01:20.591504] [W] OptiScaler v10.0.0-dev loaded\n", encoding="utf-8")
        now = helper.datetime(2026, 9, 13, 0, 2).timestamp()
        os.utime(opti, (now, now))
        os.utime(presr, (now - 30, now - 30))
        summary = helper.summarize_presr(f.game_dir, tick_reference=(60_000, now))
        self.assertTrue(summary["pre_sr_active"])
        self.assertTrue(summary["pass_ticks_correlated"])
        self.assertEqual(summary["cost_samples"], 0)
        self.assertEqual(summary["passes_completed"], 1)

    def test_diagnose_rejects_pass_ticks_before_startup_or_from_another_boot(self):
        f = Fixture()
        self.addCleanup(f.cleanup)
        presr = f.game_dir / "amd_presr.log"
        opti = f.game_dir / "OptiScaler.log"
        opti.write_text("[00:01:20.591504] [W] OptiScaler v10.0.0-dev loaded\n", encoding="utf-8")
        now = helper.datetime(2026, 9, 13, 0, 2).timestamp()
        os.utime(opti, (now, now))
        for ticks, file_age, reference_ticks in ((10_000, 0, 60_000), (70_000, 0, 60_000), (30_000, 20, 5_000)):
            with self.subTest(ticks=ticks, file_age=file_age, reference_ticks=reference_ticks):
                presr.write_text(f"{ticks - 1000} HIP runtime: fixture HIP runtime\n{ticks} Completed AMD pre-SR passes=1 at 320x180\n", encoding="utf-8")
                os.utime(presr, (now - file_age, now - file_age))
                summary = helper.summarize_presr(f.game_dir, tick_reference=(reference_ticks, now))
                self.assertFalse(summary["pre_sr_active"])
                self.assertFalse(summary["pass_ticks_correlated"])

    def test_diagnose_correlated_pass_must_follow_the_last_fault(self):
        f = Fixture()
        self.addCleanup(f.cleanup)
        presr = f.game_dir / "amd_presr.log"
        opti = f.game_dir / "OptiScaler.log"
        opti.write_text("[00:01:20.591504] [W] OptiScaler v10.0.0-dev loaded\n", encoding="utf-8")
        now = helper.datetime(2026, 9, 13, 0, 2).timestamp()
        os.utime(opti, (now, now))
        base = "25000 HIP runtime: fixture HIP runtime\n30000 Completed AMD pre-SR passes=1 at 320x180\n40000 HIP completion timeout\n"
        for recovery, active in (("", False), ("Completed AMD pre-SR passes=1\n", False), ("45000 Completed AMD pre-SR passes=1 at 320x180\n", True)):
            with self.subTest(recovery=recovery):
                presr.write_text(base + recovery, encoding="utf-8")
                written = now - (15 if active else 20)
                os.utime(presr, (written, written))
                summary = helper.summarize_presr(f.game_dir, tick_reference=(60_000, now))
                self.assertEqual(summary["pre_sr_active"], active)
                self.assertEqual(summary["pass_ticks_correlated"], active)

    def test_diagnose_correlated_pass_preserves_untimed_fault(self):
        f = Fixture()
        self.addCleanup(f.cleanup)
        presr = f.game_dir / "amd_presr.log"
        opti = f.game_dir / "OptiScaler.log"
        presr.write_text("25000 HIP runtime: fixture\n30000 Completed AMD pre-SR passes=1\nHIP completion timeout\n", encoding="utf-8")
        opti.write_text("[00:01:20.591504] OptiScaler v10.0.0-dev loaded\n", encoding="utf-8")
        now = helper.datetime(2026, 9, 13, 0, 2).timestamp()
        os.utime(opti, (now, now))
        os.utime(presr, (now - 30, now - 30))
        summary = helper.summarize_presr(f.game_dir, tick_reference=(60_000, now))
        self.assertFalse(summary["pre_sr_active"])
        self.assertEqual(summary["last_fault"], "HIP completion timeout")

    def test_diagnose_latest_amd_startup_discards_previous_completion_and_fault(self):
        f = Fixture()
        self.addCleanup(f.cleanup)
        presr = f.game_dir / "amd_presr.log"
        opti = f.game_dir / "OptiScaler.log"
        opti.write_text("[00:01:20.591504] OptiScaler v10.0.0-dev loaded\n", encoding="utf-8")
        now = helper.datetime(2026, 9, 13, 0, 2).timestamp()
        os.utime(opti, (now, now))
        old = "10000 HIP runtime: previous\n11000 Completed AMD pre-SR passes=3\n12000 HIP completion timeout\n"
        startup = "25000 HIP runtime: current\n30000 Initialized independent AMD pass 1\n"
        for completed in (False, True):
            with self.subTest(completed=completed):
                presr.write_text(old + startup + ("30000 Completed AMD pre-SR passes=1\n" if completed else ""), encoding="utf-8")
                os.utime(presr, (now - 30, now - 30))
                summary = helper.summarize_presr(f.game_dir, tick_reference=(60_000, now))
                self.assertEqual(summary["pre_sr_active"], completed)
                self.assertEqual(summary["passes_completed"], 1 if completed else None)
                self.assertIsNone(summary["last_fault"])

    def test_presr_clock_correlation_requires_current_startup_and_write_agreement(self):
        now = 1_800_000_000.0
        reference = (60_000, now)
        startup = "25000 HIP runtime: fixture\n"
        complete = "30000 Completed AMD pre-SR passes=1\n"
        for text, written, start in (
            (complete, now - 30, now - 40),
            ("HIP runtime: fixture\n" + complete, now - 30, now - 40),
            (startup + complete, now, now - 40),
            (startup + complete, now - 30, now - 34.999),
            (startup + "70000 Recorded pre-SR\n" + complete, now - 30, now - 40),
            (startup + "10000 Recorded pre-SR\n" + complete, now - 30, now - 40),
            (startup + "9" * 5000 + " Recorded pre-SR\n" + complete, now - 30, now - 40),
            (startup + "Completed AMD pre-SR passes=1\n30000 Workers stopped outside loader lock\n", now - 30, now - 40),
            (startup + complete, now - 30, now - 90),
            (startup + complete, now - 90, now - 40),
        ):
            with self.subTest(text=text, written=written, start=start):
                self.assertFalse(helper.scope_presr_session(text, written, start, reference)[1])
        self.assertTrue(helper.scope_presr_session(startup + complete, now - 30, now - 40, reference)[1])

    def test_diagnose_presr_ignores_pass_log_predating_new_startup(self):
        f = Fixture()
        self.addCleanup(f.cleanup)
        presr = f.game_dir / "amd_presr.log"
        presr.write_text("AMD engine initialization failed\n", encoding="utf-8")
        opti = f.game_dir / "OptiScaler.log"
        opti.write_text("[00:01:20.591504] [W] OptiScaler v10.0.0-dev loaded\nDLSS-NR cost: 11.00 ms total = 9.00 ms model\n", encoding="utf-8")
        old = helper.datetime(2026, 9, 12, 23, 50).timestamp()
        current = helper.datetime(2026, 9, 13, 0, 4).timestamp()
        os.utime(presr, (old, old))
        os.utime(opti, (current, current))
        summary = helper.summarize_presr(f.game_dir)
        self.assertTrue(summary["pre_sr_active"])
        self.assertIsNone(summary["last_fault"])
        self.assertIn("amd_presr.log", summary["stale_logs"])

    def test_diagnose_checks_only_configured_pass_dlls(self):
        f = Fixture()
        self.addCleanup(f.cleanup)
        install_args = helper.parse_args(["--game", str(f.exe), "--install", "--route", "optiscaler-presr", "--package", str(f.package), "--weights", str(f.weights)])
        helper.install_optiscaler(install_args, version_reader=fake_fork)
        (f.game_dir / "dlssnr_amd_pass3.dll").unlink()
        args = helper.parse_args(["--game", str(f.exe), "--diagnose"])
        self.assertEqual(helper.diagnose(args)["pre_sr"]["missing_install_files"], [])
        (f.game_dir / "OptiScaler.ini").write_text("[DlssNr]\nPasses=3\n", encoding="utf-8")
        self.assertIn("dlssnr_amd_pass3.dll", helper.diagnose(args)["pre_sr"]["missing_install_files"])

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

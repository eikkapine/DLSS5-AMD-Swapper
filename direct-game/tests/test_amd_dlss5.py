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


def make_package(parent: Path) -> Path:
    root = parent / "OptiScaler-AMD-PreSR-Multipass-v1.2"
    (root / "OptiScaler").mkdir(parents=True)
    write_pe(root / "OptiScaler.dll")
    for index in (1, 2, 3):
        write_pe(root / f"dlssnr_amd_pass{index}.dll", b"dlssnr_amd")
    (root / "OptiScaler.ini").write_text("[Upscalers]\nDx12Upscaler=auto\n\n[DlssNr]\nEnabled=auto\n", encoding="utf-8")
    (root / "dlssnr_on_amd_weights.bin").write_text("version https://git-lfs.github.com/spec/v1\n", encoding="utf-8")
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


class InstallTests(unittest.TestCase):
    def test_install_and_remove(self):
        f = Fixture()
        self.addCleanup(f.cleanup)
        args = helper.parse_args(["--game", str(f.exe), "--install", "--route", "optiscaler-presr", "--package", str(f.package), "--weights", str(f.weights)])
        manifest = helper.install_optiscaler(args, version_reader=fake_fork)
        self.assertEqual(manifest["route"], "amd-optiscaler-presr")
        self.assertEqual(manifest["schema_version"], 3)
        for name in ("dxgi.dll", "OptiScaler.ini", "dlssnr_amd_pass3.dll", "dlssnr_on_amd_weights.bin", os.path.join("OptiScaler", "amd_fidelityfx_upscaler_dx12.dll")):
            self.assertTrue((f.game_dir / name).exists(), name)
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


if __name__ == "__main__":
    unittest.main()

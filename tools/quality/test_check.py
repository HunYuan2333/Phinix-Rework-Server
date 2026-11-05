import importlib.util
import os
from pathlib import Path
import sys
import subprocess
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("quality_check", Path(__file__).with_name("check.py"))
check = importlib.util.module_from_spec(spec)
spec.loader.exec_module(check)


class SupervisorTests(unittest.TestCase):
    def test_mutation_requires_disposable_snapshot(self):
        with tempfile.TemporaryDirectory() as directory:
            evidence = Path(directory) / "never-created"
            product = check.ROOT / "Extensions/Trade/Server/PhinixFrameworkTradeServerService.cs"
            before = check.digest(product)
            result = subprocess.run([sys.executable, str(Path(check.__file__)), "--mutation", "partial-batch",
                                     "--output", str(evidence)], capture_output=True, text=True)
            self.assertEqual(result.returncode, 2)
            self.assertIn("requires --isolated", result.stderr)
            self.assertFalse(evidence.exists())
            self.assertEqual(before, check.digest(product))

    def test_mutations_follow_the_explicit_offer_adapter_boundary(self):
        # Only disposable source copies: all three negative controls remain executable
        # after P5 moved historical wire normalization out of the service.
        import shutil
        for kind in ["partial-batch", "unscoped-cache", "early-consume"]:
            with tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                target = root / "Extensions/Trade/Server"
                target.mkdir(parents=True)
                for name in ["LegacyTradeOfferAdapter.cs", "PhinixFrameworkTradeServerService.cs"]:
                    shutil.copyfile(check.ROOT / "Extensions/Trade/Server" / name, target / name)
                result = check.mutate_snapshot(root, kind)
                self.assertNotEqual(result["originalSha256"], result["mutatedSha256"])

    def test_nonzero_is_failure(self):
        record, output = check.supervise([sys.executable, "-c", "print('synthetic'); raise SystemExit(7)"], Path.cwd(), 3)
        self.assertFalse(record["passed"])
        self.assertEqual(record["exitCode"], 7)
        self.assertIn("synthetic", output)

    def test_timeout_kills_descendant(self):
        with tempfile.TemporaryDirectory() as directory:
            pid_file = Path(directory) / "child.pid"
            source = ("import subprocess,sys,time; "
                      "p=subprocess.Popen([sys.executable,'-c','import time; time.sleep(30)']); "
                      f"open({str(pid_file)!r},'w').write(str(p.pid)); time.sleep(30)")
            record, _ = check.supervise([sys.executable, "-c", source], Path.cwd(), 1)
            self.assertFalse(record["passed"])
            self.assertTrue(record["timedOut"])
            child = int(pid_file.read_text())
            # A killed orphan may briefly remain a zombie until reaped by init.
            stat = Path(f"/proc/{child}/stat")
            self.assertTrue(not stat.exists() or stat.read_text().split()[2] == "Z")

    def test_artifact_check_rejects_missing_and_game_dll(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            with self.assertRaises(RuntimeError):
                check.check_artifacts(root)
            for name in ["PhinixServer.dll", "PhinixServer.deps.json", "PhinixServer.runtimeconfig.json", "LiteNetLib.dll", "Autofac.dll", "Stateless.dll", "ServerStorage.dll",
                         "Microsoft.Data.Sqlite.dll", "SQLitePCLRaw.core.dll", "SQLitePCLRaw.batteries_v2.dll",
                         "SQLitePCLRaw.provider.e_sqlite3.dll", "runtimes/linux-x64/native/libe_sqlite3.so",
                         "Extensions/ChatExtension.dll", "Extensions/ChatExtension.Server.dll",
                         "Extensions/TradeExtension.dll", "Extensions/TradeExtension.Server.dll", "Extensions/LegacyAdapter.Server.dll"]:
                target = root / name
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(b"synthetic artifact shape only")
            self.assertEqual(len(check.check_artifacts(root)), 17)
            (root / "UnityEngine.dll").write_bytes(b"forbidden")
            with self.assertRaises(RuntimeError):
                check.check_artifacts(root)


class CoverageEvidenceTests(unittest.TestCase):
    def report(self, directory, packages='TradeExtension.Server', valid=10, covered=4, source='Extensions/Trade/Server/Store.cs'):
        path = Path(directory) / 'coverage.xml'
        path.write_text(f'<coverage lines-valid="{valid}" lines-covered="{covered}" branches-valid="2" branches-covered="1"><packages>' +
                        ''.join(f'<package name="{name}"><classes><class filename="{source}"/></classes></package>' for name in packages.split(',')) +
                        '</packages></coverage>')
        return path

    def test_actual_counts_and_explicit_scope(self):
        with tempfile.TemporaryDirectory() as directory:
            result = check.summarize_coverage(self.report(directory), ['TradeExtension.Server'])
            self.assertEqual(result['lines-covered'], 4)
            self.assertEqual(result['lines-valid'], 10)
            self.assertEqual(result['linePercent'], 40)

    def test_missing_empty_out_of_scope_and_vendor_reports_fail(self):
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaises(FileNotFoundError):
                check.summarize_coverage(Path(directory) / 'missing.xml', ['TradeExtension.Server'])
            for values in [dict(valid=0, covered=0), dict(covered=0), dict(covered=11),
                           dict(packages='Google.Protobuf'), dict(source='/source/Dependencies/vendor.cs')]:
                with self.subTest(values=values), self.assertRaises(ValueError):
                    check.summarize_coverage(self.report(directory, **values), ['TradeExtension.Server'])

    def test_instrumentation_modes_require_isolated_copy(self):
        for option in ['--coverage', '--fresh-packages']:
            with tempfile.TemporaryDirectory() as directory:
                output = Path(directory) / 'never-created'
                result = subprocess.run([sys.executable, str(Path(check.__file__)), option, '--output', str(output)],
                                        capture_output=True, text=True)
                self.assertEqual(result.returncode, 2)
                self.assertIn('requires --isolated', result.stderr)
                self.assertFalse(output.exists())


if __name__ == "__main__":
    unittest.main()

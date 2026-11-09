#!/usr/bin/env python3
"""Serial, fail-closed server checks. No player data; transport tests bind ephemeral UDP ports."""
import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import shutil
import signal
import subprocess
import sys
import tempfile
import time

# Also imported by the supervisor suite via importlib.
sys.path.insert(0, str(Path(__file__).resolve().parent))
from coverage_report import summarize as summarize_coverage

ROOT = Path(__file__).resolve().parents[2]


def git(root, *args):
    return subprocess.check_output(["git", "-C", str(root), *args]).decode().rstrip("\n")


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def supervise(command, cwd, timeout, env=None):
    """Own the whole process group; a timeout/failure can never be reported as success."""
    started = time.monotonic()
    with tempfile.TemporaryFile() as output:
        process = subprocess.Popen(command, cwd=cwd, env=env, stdout=output,
                                   stderr=subprocess.STDOUT, start_new_session=True)
        timed_out = False
        try:
            process.wait(timeout=timeout)
        except subprocess.TimeoutExpired:
            timed_out = True
            os.killpg(process.pid, signal.SIGKILL)
            process.wait()
        except BaseException:
            os.killpg(process.pid, signal.SIGKILL)
            process.wait()
            raise
        output.seek(0)
        log = output.read().decode(errors="replace")
    return {"command": command, "exitCode": process.returncode,
            "timedOut": timed_out, "seconds": round(time.monotonic() - started, 3),
            "passed": not timed_out and process.returncode == 0}, log


def repositories(root):
    result = []
    for base in [root] + [root / line.split()[1] for line in
                          git(root, "submodule", "status", "--recursive").splitlines()]:
        result.append({"path": str(base.relative_to(root)) or ".",
                       "sha": git(base, "rev-parse", "HEAD"),
                       "branch": git(base, "branch", "--show-current"),
                       "status": git(base, "status", "--short"),
                       "gitlinks": git(base, "ls-files", "--stage", "--", "Dependencies")})
    return result


def snapshot(root, destination, repos):
    """Copy only versioned/build-source inputs, never bin/obj or runtime data.

    Includes uncommitted source changes (hashed), not a claim of a clean Git revision.
    An actual clean checkout uses the same entry point without --isolated.
    """
    hashes = {}
    suffixes = {".cs", ".csproj", ".props", ".targets", ".proto", ".resx", ".snk", ".dll", ".py"}
    root_inputs = {"Directory.Build.props", "Directory.Build.targets", "nuget.config"}
    for repo in repos:
        base = root / repo["path"]
        paths = git(base, "ls-files", "--cached", "--others", "--exclude-standard", "-z").split("\0")
        for name in paths:
            if not name:
                continue
            source = base / name
            relative = source.relative_to(root)
            if not source.is_file() or source.is_symlink():
                continue
            if repo["path"] == "." and relative.parts[0] not in {"Server", "Extensions", "Tests", "tools"} and name not in root_inputs:
                continue
            if {"bin", "obj", "GameDlls", "Output", ".git"}.intersection(relative.parts):
                continue
            fixture = str(relative).startswith(("Tests/Fixtures/OriginalLoginChat/", "Tests/Fixtures/OriginalTrade/", "Tests/Fixtures/HistoricalReworkLogin/", "Tests/Fixtures/FrameworkControl/")) and source.name in {"normalized.json", "provenance.json"}
            if source.suffix not in suffixes and source.name not in root_inputs | {"global.json", "packages.lock.json", "architecture-scope.json", "coverage-scope.json"} and not fixture:
                continue
            target = destination / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(source, target)
            hashes[str(relative)] = digest(target)
    return hashes


def check_artifacts(output):
    required = ["PhinixServer.dll", "PhinixServer.deps.json", "PhinixServer.runtimeconfig.json",
                "LiteNetLib.dll", "Autofac.dll", "Stateless.dll", "ServerStorage.dll", "Microsoft.Data.Sqlite.dll",
                "SQLitePCLRaw.core.dll", "SQLitePCLRaw.batteries_v2.dll", "SQLitePCLRaw.provider.e_sqlite3.dll", "Extensions/ChatExtension.dll", "Extensions/ChatExtension.Server.dll",
                "Extensions/TradeExtension.dll", "Extensions/TradeExtension.Server.dll", "Extensions/LegacyAdapter.Server.dll"]
    if sys.platform.startswith("linux") and platform.machine() == "x86_64":
        required.append("runtimes/linux-x64/native/libe_sqlite3.so")
    missing = [name for name in required if not (output / name).is_file()]
    forbidden = [str(p.relative_to(output)) for p in output.rglob("*.dll")
                 if p.name.startswith(("Assembly-CSharp", "Unity")) or
                 p.name in {"mscorlib.dll", "com.rlabrecque.steamworks.net.dll", "FsCheck.dll",
                            "ServerReferenceModel.dll", "ServerModelTests.dll", "ServerArchitectureTests.dll"}]
    if missing or forbidden:
        raise RuntimeError(f"Artifact check failed: missing={missing}, forbidden={forbidden}")
    return {str(p.relative_to(output)): digest(p) for p in sorted(output.rglob("*")) if p.is_file()}


def mutate_snapshot(root, kind):
    """Negative control in a disposable build copy only; never a production fault hook."""
    path = root / ("Extensions/Trade/Server/LegacyTradeOfferAdapter.cs" if kind == "partial-batch"
                   else "Extensions/Trade/Server/PhinixFrameworkTradeServerService.cs")
    before = digest(path)
    source = path.read_text()
    changes = {
        "partial-batch": ("var item = resolveReference(reference);\n                    if (!valid(item)) return false;",
                          "var item = resolveReference(reference);\n                    if (item == null) continue;\n                    if (!valid(item)) return false;"),
        "unscoped-cache": ("(context.SenderUuid, context.ConnectionId, context.SessionId, packetId);",
                           '("unscoped", "unscoped", "unscoped", packetId);'),
        "early-consume": ("FrameworkItemPayload Resolve(string id) => HasCacheScope(context) && itemPacketCache.TryGetValue(CacheKey(context, id), out var cached)\n                ? cached.Payload : null;",
                          "FrameworkItemPayload Resolve(string id) { if (!HasCacheScope(context) || !itemPacketCache.TryGetValue(CacheKey(context, id), out var cached)) return null; itemPacketCache.Remove(CacheKey(context, id)); return cached.Payload; }")
    }
    old, new = changes[kind]
    if source.count(old) != 1:
        raise RuntimeError("Mutation target moved; review the negative control before using it")
    path.write_text(source.replace(old, new))
    return {"id": kind, "path": str(path.relative_to(root)), "originalSha256": before, "mutatedSha256": digest(path)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--suite", choices=["quick", "stage", "regressions"], default="quick")
    parser.add_argument("--isolated", action="store_true", help="rebuild a hashed source snapshot without existing bin/obj")
    parser.add_argument("--output", type=Path, required=True, help="new evidence directory; refuses overwrite")
    parser.add_argument("--timeout", type=int, default=180, help="deadline per child process in seconds")
    parser.add_argument("--fresh-packages", action="store_true", help="empty package/HTTP caches and public-only NuGet config; requires --isolated")
    parser.add_argument("--coverage", action="store_true", help="pinned test-only Coverlet pilot; requires --isolated")
    parser.add_argument("--mutation", choices=["partial-batch", "unscoped-cache", "early-consume"], help="negative control, disposable source snapshot only")
    parser.add_argument("--model-property", choices=["all", "batch", "scope", "sequence"], default="all")
    args = parser.parse_args()
    if args.timeout <= 0:
        parser.error("timeout must be positive")
    if (args.fresh_packages or args.coverage) and not args.isolated:
        parser.error("fresh-packages/coverage requires --isolated")
    if args.mutation and not args.isolated:
        parser.error("mutation requires --isolated; never mutate a working checkout")
    evidence = args.output.resolve()
    evidence.mkdir(parents=True, exist_ok=False)
    manifest = {"schema": 1, "suite": args.suite, "layer": "L1/L2 component only",
                "profile": "existing-rework-component", "seed": [20261015, 20261017], "faultIds": [],
                "coverage": {"requested": args.coverage, "collected": False, "reason": "Not collected; no coverage percentage claimed"},
                "checks": [], "passed": False}
    manifest["runnerSha256"] = digest(Path(__file__))
    manifest["arguments"] = sys.argv[1:]
    manifest["nugetAudit"] = "Disabled for build reproducibility; this run is not a package vulnerability audit"
    try:
        repos = repositories(ROOT)
        manifest["repositories"] = repos
        manifest["configurationInputs"] = {name: digest(ROOT / name) for name in
            ["Dockerfile", ".gitmodules", ".github/workflows/server-quality.yml", ".github/workflows/docker.yml"]}
        submodules = git(ROOT, "submodule", "status", "--recursive")
        if any(line.startswith(("+", "-", "U")) for line in submodules.splitlines()):
            raise RuntimeError("Submodules must be initialized at exact indexed gitlinks (no --remote)")
        manifest["environment"] = {"platform": platform.platform(), "cpuCount": os.cpu_count(),
                                    "python": platform.python_version(), "diskFreeBytes": shutil.disk_usage(evidence).free}
        manifest["sdk"] = subprocess.check_output(["dotnet", "--info"]).decode()
        # Override only in the child environment; never change protobuf's SDK pin.
        sdk_line = subprocess.check_output(["dotnet", "--list-sdks"]).decode().strip().splitlines()[-1]
        sdk_version, sdk_base = re.fullmatch(r"(\S+) \[(.+)\]", sdk_line).groups()
        env = dict(os.environ, MSBuildSDKsPath=str(Path(sdk_base) / sdk_version / "Sdks"),
                   MSBuildEnableWorkloadResolver="false", DOTNET_CLI_TELEMETRY_OPTOUT="1")
        manifest["sdkOverride"] = env["MSBuildSDKsPath"]
        with tempfile.TemporaryDirectory(prefix="phinix-quality-") as work:
            build_root = Path(work) / "source" if args.isolated else ROOT
            build_root.mkdir(parents=True, exist_ok=True)
            # Nested fixed submodules have their own targets; apply the server's
            # compatibility/security overrides throughout this isolated graph.
            env["DirectoryBuildTargetsPath"] = str(build_root / "Directory.Build.targets")
            if args.isolated:
                manifest["sourceInputs"] = snapshot(ROOT, build_root, repos)
            else:
                # Hash source inputs as well, without modifying the checkout.
                manifest["sourceInputs"] = snapshot(ROOT, Path(work) / "inventory", repos)
            restore_options = []
            if args.fresh_packages:
                caches = Path(work) / "nuget"
                for key, folder in [("NUGET_PACKAGES", "packages"), ("NUGET_HTTP_CACHE_PATH", "http"),
                                    ("NUGET_PLUGINS_CACHE_PATH", "plugins")]:
                    location = caches / folder
                    location.mkdir(parents=True)
                    env[key] = str(location)
                config = Path(work) / "public-nuget.config"
                config.write_text('<configuration><packageSources><clear/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources><fallbackPackageFolders><clear/></fallbackPackageFolders></configuration>')
                restore_options = ["--configfile", str(config), "--no-http-cache", "--force",
                                   "-p:RestoreFallbackFolders=", "-p:RestoreAdditionalProjectSources="]
                manifest["packageRestore"] = {"mode": "empty-private-caches-public-source", "initialFiles": 0,
                                              "configSha256": digest(config)}
            else:
                manifest["packageRestore"] = {"mode": "existing-environment-cache"}
            if args.mutation:
                manifest["mutation"] = mutate_snapshot(build_root, args.mutation)
                manifest["sourceInputs"][manifest["mutation"]["path"]] = manifest["mutation"]["mutatedSha256"]

            def step(name, command, cwd=build_root, expected=None):
                print(name, flush=True)
                record, log = supervise(command, cwd, args.timeout, env)
                record["name"] = name
                if expected and not re.search(expected, log):
                    record["passed"] = False
                    record["reason"] = "Missing expected test completion marker"
                (evidence / (name + ".txt")).write_text(log)
                manifest["checks"].append(record)
                if not record["passed"]:
                    print(log[-3000:], file=sys.stderr)
                return record["passed"]

            def build(name, project):
                if not step(name + "-restore", ["dotnet", "restore", project, "--disable-parallel", "-m:1", "-p:BuildInParallel=false", "-p:NuGetAudit=false", *restore_options]):
                    return False
                return step(name + "-build", ["dotnet", "build", project, "-c", "Release", "--no-restore",
                                             "-m:1", "-p:BuildInParallel=false"])

            ok = step("supervisor-test", [sys.executable, "-m", "unittest", "discover", "-s", "tools/quality", "-p", "test_*.py", "-v"], expected=r"(?m)^OK$")
            ok = step("golden-test", [sys.executable, "-m", "unittest", "discover", "-s", "Tests/LegacyGoldenTests", "-v"], expected=r"(?m)^OK$") and ok
            # Preserve the old console runner and its exit semantics.
            if not build("server", "Server/Server.csproj"):
                return 1
            if build("secure-transport", "Tests/ServerSecureTransportTests/ServerSecureTransportTests.csproj"):
                ok = step("secure-transport-test", ["dotnet", "Tests/ServerSecureTransportTests/bin/Release/net10.0/ServerSecureTransportTests.dll"],
                          expected=r'"passed":true') and ok
            else:
                ok = False
            if build("identity", "Tests/ServerIdentityTests/ServerIdentityTests.csproj"):
                ok = step("identity-test", ["dotnet", "Tests/ServerIdentityTests/bin/Release/net10.0/ServerIdentityTests.dll"],
                          expected=r'"passed":true') and ok
            else:
                ok = False
            if build("tls-host", "Tests/ServerTlsHostTests/ServerTlsHostTests.csproj"):
                ok = step("tls-host-test", ["dotnet", "Tests/ServerTlsHostTests/bin/Release/net10.0/ServerTlsHostTests.dll"],
                          expected=r'"passed":true') and ok
            else:
                ok = False
            if not build("pipeline", "Tests/Phase35ServerRuntimeTests/Phase35ServerRuntimeTests.csproj"):
                return 1
            ok = step("pipeline-test", ["dotnet", "Tests/Phase35ServerRuntimeTests/bin/Release/net10.0/Phase35ServerRuntimeTests.dll"],
                      expected=r"PASS ServerFull 21 groups; [0-9]+ assertions") and ok
            if build("lifecycle", "Tests/ServerLifecycleTests/ServerLifecycleTests.csproj"):
                ok = step("lifecycle-test", ["dotnet", "Tests/ServerLifecycleTests/bin/Release/net10.0/ServerLifecycleTests.dll"],
                          expected=r"LIFECYCLE RESULT passed;") and ok
                ok = step("relay-network-test", ["dotnet", "Tests/ServerLifecycleTests/bin/Release/net10.0/ServerLifecycleTests.dll", "--relay-network"],
                          expected=r"RELAY NETWORK RESULT passed;") and ok
            else:
                ok = False
            if build("snapshot", "Tests/ServerSnapshotTests/ServerSnapshotTests.csproj"):
                ok = step("snapshot-test", ["dotnet", "Tests/ServerSnapshotTests/bin/Release/net10.0/ServerSnapshotTests.dll"],
                          expected=r"SNAPSHOT RESULT passed;") and ok
            else:
                ok = False
            if build("shared", "Dependencies/Phinix.Common/Tests/Phase35CoreRuntimeTests/Phase35CoreRuntimeTests.csproj"):
                ok = step("shared-test", ["dotnet", "Dependencies/Phinix.Common/Tests/Phase35CoreRuntimeTests/bin/Release/net10.0/Phase35CoreRuntimeTests.dll"],
                          expected=r"PASS Shared 4 cases;") and ok
            else:
                ok = False
            if args.suite in {"quick", "stage", "regressions"}:
                project = "Tests/ServerSafetyRegressionTests/ServerSafetyRegressionTests.csproj"
                if build("safety", project):
                    ok = step("safety-test", ["dotnet", "Tests/ServerSafetyRegressionTests/bin/Release/net10.0/ServerSafetyRegressionTests.dll"],
                              expected=r"(?m)^RESULT cases=\d+ failed=0$") and ok
                else:
                    ok = False
            if args.coverage:
                scope_path = build_root / "tools/quality/coverage-scope.json"
                scope = json.loads(scope_path.read_text())
                collector = Path(work) / "collector"
                command = ["dotnet", "tool", "install", scope["collector"], "--version", scope["version"],
                           "--tool-path", str(collector)]
                if args.fresh_packages:
                    command += ["--configfile", str(config), "--no-cache"]
                if not step("coverage-install", command):
                    return 1
                package = next(collector.rglob("coverlet.console." + scope["version"] + ".nupkg"))
                package_hash = base64.b64encode(hashlib.sha512(package.read_bytes()).digest()).decode()
                manifest["coverage"]["actualPackageSha512"] = package_hash
                if package_hash != scope["packageSha512"]:
                    raise RuntimeError("Collector package content hash mismatch")
                dll = build_root / "Tests/ServerSafetyRegressionTests/bin/Release/net10.0/ServerSafetyRegressionTests.dll"
                report = evidence / "safety.cobertura.xml"
                before = {str(p.relative_to(dll.parent)): digest(p) for p in dll.parent.glob("*.dll")}
                command = [str(collector / "coverlet"), str(dll), "--target", "dotnet", "--targetargs", str(dll),
                           "--format", "cobertura", "--output", str(report)]
                for assembly in scope["assemblies"]:
                    command += ["--include", "[" + assembly + "]*"]
                for pattern in scope["excludedFiles"]:
                    command += ["--exclude-by-file", pattern]
                if not step("coverage-safety-test", command, expected=r"(?m)^RESULT cases=\d+ failed=0$"):
                    return 1
                after = {str(p.relative_to(dll.parent)): digest(p) for p in dll.parent.glob("*.dll")}
                if before != after:
                    raise RuntimeError("Collector did not restore original assemblies")
                summary = summarize_coverage(report, scope["assemblies"])
                manifest["coverage"] = {"requested": True, "collected": True, "collector": scope["collector"],
                    "version": scope["version"], "packageSha512": package_hash, "scopeSha256": digest(scope_path),
                    "scope": scope["scope"], "reportSha256": digest(report), "assembliesRestored": True, **summary}
            if build("original-trade-offer", "Tests/ServerOriginalTradeAdapterTests/ServerOriginalTradeAdapterTests.csproj"):
                ok = step("original-trade-offer-test", ["dotnet", "Tests/ServerOriginalTradeAdapterTests/bin/Release/net10.0/ServerOriginalTradeAdapterTests.dll"],
                          expected=r"ORIGINAL OFFER RESULT passed;") and ok
            else:
                ok = False
            if build("original-chat", "Tests/ServerLegacyChatTests/ServerLegacyChatTests.csproj"):
                ok = step("original-chat-test", ["dotnet", "Tests/ServerLegacyChatTests/bin/Release/net10.0/ServerLegacyChatTests.dll"],
                          expected=r"ORIGINAL CHAT RESULT passed;") and ok
            else:
                ok = False
            if build("journal", "Tests/ServerJournalTests/ServerJournalTests.csproj"):
                ok = step("journal-test", [sys.executable, "tools/quality/journal_check.py", "Tests/ServerJournalTests/bin/Release/net10.0/ServerJournalTests.dll"],
                          expected=r"JOURNAL ORACLE RESULT passed;") and ok
            else:
                ok = False
            if build("trade-persistence", "Tests/ServerTradePersistenceTests/ServerTradePersistenceTests.csproj"):
                ok = step("trade-persistence-test", [sys.executable, "tools/quality/trade_persistence_check.py", "Tests/ServerTradePersistenceTests/bin/Release/net10.0/ServerTradePersistenceTests.dll"],
                          expected=r"TRADE ORACLE RESULT passed;") and ok
            else:
                ok = False
            if build("model", "Tests/ServerModelTests/ServerModelTests.csproj"):
                model_dll = "Tests/ServerModelTests/bin/Release/net10.0/ServerModelTests.dll"
                ok = step("framework-negative-control", ["dotnet", model_dll, "--framework-negative-control"],
                          expected=r'"passed":true') and ok
                ok = step("model-test", ["dotnet", model_dll, "--property", args.model_property],
                          expected=r"(?m)^MODEL RESULT failed=0$") and ok
            else:
                ok = False
            if build("architecture", "Tests/ServerArchitectureTests/ServerArchitectureTests.csproj"):
                ok = step("architecture-test", ["dotnet", "Tests/ServerArchitectureTests/bin/Release/net10.0/ServerArchitectureTests.dll"],
                          expected=r"ARCHITECTURE RESULT passed;") and ok
            else:
                ok = False
            if args.suite == "stage":
                if step("publish", ["dotnet", "publish", "Server/Server.csproj", "-c", "Release", "--no-restore",
                                    "--no-self-contained", "-m:1", "-p:BuildInParallel=false", "-o", str(Path(work) / "publish")]):
                    # The existing project copies official plugins on Build, not Publish.
                    shutil.copytree(build_root / "Server/bin/Release/net10.0/Extensions", Path(work) / "publish/Extensions", dirs_exist_ok=True)
                    manifest["artifacts"] = check_artifacts(Path(work) / "publish")
                    ok = step("p3-process-test", [sys.executable, "tools/quality/p3_acceptance_probe.py",
                              "--server", str(Path(work) / "publish/PhinixServer.dll"),
                              "--probe", str(build_root / "Tests/ServerLifecycleTests/bin/Release/net10.0/ServerLifecycleTests.dll"),
                              "--output", str(evidence / "p3-network")], expected=r"P3 PROCESS RESULT passed;") and ok
                else:
                    ok = False
            if args.fresh_packages:
                folders = {folder for assets in build_root.rglob("project.assets.json")
                           for folder in json.loads(assets.read_text()).get("packageFolders", {})}
                if {Path(folder).resolve() for folder in folders} != {Path(env["NUGET_PACKAGES"]).resolve()}:
                    raise RuntimeError("Restore used unexpected package folders")
                manifest["packageRestore"]["assetPackageFolders"] = sorted(folders)
                manifest["packageRestore"]["restoredPackages"] = {
                    str(p.parent.relative_to(Path(env["NUGET_PACKAGES"]))): p.read_text().strip()
                    for p in Path(env["NUGET_PACKAGES"]).rglob("*.nupkg.sha512")}
                if not manifest["packageRestore"]["restoredPackages"]:
                    raise RuntimeError("Fresh restore produced no package evidence")
            manifest["passed"] = ok
            return 0 if ok else 1
    except Exception as error:
        manifest["error"] = str(error)
        print(str(error), file=sys.stderr)
        return 1
    finally:
        (evidence / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")


if __name__ == "__main__":
    sys.exit(main())

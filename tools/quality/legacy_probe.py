#!/usr/bin/env python3
"""Build fixed original modules in /tmp and capture synthetic, redacted login/chat wire.

No working checkout, gitlink or vendored file is changed. Requires Mono and acquired sources.
"""
import argparse
import hashlib
import io
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile

from check import ROOT, digest, git, supervise

ORIGINAL = "ce82b8f2cd57718f05de86458eebd3320f35c4d8"
PROTOBUF = "dd06cc6c8f906b760ef224a085f45e7694dc426a"


def archive(repository, sha, paths, destination):
    subprocess.check_call(["git", "-C", str(repository), "cat-file", "-e", sha + "^{commit}"])
    data = subprocess.check_output(["git", "-C", str(repository), "archive", sha, *paths])
    destination.mkdir(parents=True, exist_ok=True)
    with tarfile.open(fileobj=io.BytesIO(data)) as files:
        files.extractall(destination, filter="data")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--original-source", required=True, type=Path)
    parser.add_argument("--protobuf-source", required=True, type=Path)
    parser.add_argument("--package-cache", required=True, type=Path, help="restored LiteNetLib 0.7.7.3 and Pastel 1.3.1")
    parser.add_argument("--framework-root", required=True, type=Path, help="targeting pack build/ directory containing .NETFramework/v4.7.2")
    parser.add_argument("--scenario", choices=["login-chat", "trade"], default="login-chat")
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    manifest = {"schema": 1, "profile": "Original", "originalSha": ORIGINAL, "protobufSha": PROTOBUF,
                "scenario": args.scenario, "passed": False, "checks": [], "seed": None, "faultIds": [],
                "scope": "Original module build and real UDP smoke; not upgraded Server compatibility or real game delivery"}
    try:
        sdk = subprocess.check_output(["dotnet", "--list-sdks"]).decode().strip().splitlines()[-1]
        version, sdk_base = re.fullmatch(r"(\S+) \[(.+)\]", sdk).groups()
        env = dict(os.environ, MSBuildSDKsPath=str(Path(sdk_base) / version / "Sdks"),
                   MSBuildEnableWorkloadResolver="false", DOTNET_CLI_TELEMETRY_OPTOUT="1")
        manifest["sdk"] = subprocess.check_output(["dotnet", "--info"]).decode()
        manifest["mono"] = subprocess.check_output(["mono", "--version"]).decode()
        manifest["runnerSha256"] = digest(Path(__file__))
        with tempfile.TemporaryDirectory(prefix="phinix-original-") as directory:
            work = Path(directory)
            source = work / "original"
            archive(args.original_source, ORIGINAL, ["Common"], source)
            archive(args.protobuf_source, PROTOBUF, ["csharp"], source / "Dependencies/protobuf")
            for package, name, package_version in [("litenetlib", "LiteNetLib", "0.7.7.3"), ("pastel", "Pastel", "1.3.1")]:
                shutil.copytree(args.package_cache / package / package_version, source / ".nuget" / (name + "." + package_version))
            manifest["sourceInputs"] = {str(p.relative_to(source)): digest(p) for p in source.rglob("*") if p.is_file() and p.suffix in {".cs", ".csproj", ".proto"}}
            manifest["packageInputs"] = {str(p.relative_to(source)): digest(p) for p in (source / ".nuget").rglob("*.dll")}
            producer = work / "producer"
            shutil.copytree(ROOT / "Tests/LegacyModuleProbe", producer, ignore=shutil.ignore_patterns("bin", "obj"))
            manifest["producerInputs"] = {p.name: digest(p) for p in producer.iterdir() if p.is_file()}
            private_temp = work / "runtime-temp"
            private_temp.mkdir()
            env["TMPDIR"] = str(private_temp)

            def step(name, command):
                print(name, flush=True)
                record, log = supervise(command, work, 180, env)
                record["name"] = name
                manifest["checks"].append(record)
                (output / (name + ".txt")).write_text(log)
                if not record["passed"]:
                    raise RuntimeError(name + " failed (see isolated build evidence)")

            proto = source / "Dependencies/protobuf/csharp/src/Google.Protobuf/Google.Protobuf.csproj"
            step("protobuf-restore", ["dotnet", "restore", str(proto), "--disable-parallel", "-m:1", "-p:NuGetAudit=false", "-p:TargetFrameworks=net472", "-p:TargetFramework=net472"])
            # These are invocation properties, not changes to old/vendored project files.
            flags = ["-t:Build", "-p:Configuration=Release", "-m:1", "-p:BuildInParallel=false", "-p:TargetFramework=net472",
                     "-p:TargetFrameworkRootPath=" + str(args.framework_root.resolve()) + "/",
                     "-p:EnableSourceControlManagerQueries=false", "-p:EnableSourceLink=false"]
            for module in ["Chat", "Trading"]:
                step(module.lower() + "-build", ["dotnet", "msbuild", str(source / "Common" / module / (module + ".csproj")), *flags])
            step("producer-build", ["dotnet", "build", str(producer / "LegacyModuleProbe.csproj"), "-c", "Release", "-m:1",
                                    "-p:BuildInParallel=false", "-p:NuGetAudit=false", "-p:LegacyOriginalRoot=" + str(source)])
            step("original-udp-smoke", ["mono", str(producer / "bin/Release/net472/LegacyModuleProbe.exe"), *(["--trade"] if args.scenario == "trade" else []), str(output / "capture.json")])
            capture = json.loads((output / "capture.json").read_text())
            if len(capture["fixtures"]) != (9 if args.scenario == "trade" else 3) or capture["sourceSha"] != ORIGINAL:
                raise RuntimeError("Unexpected fixture capture")
            manifest["fixtures"] = capture["fixtures"]
            manifest["binaryHashes"] = {str(p.relative_to(source)): digest(p) for p in source.rglob("*.dll") if "bin" in p.parts}
            # No credential file, private runtime directory or raw packet bytes are retained.
            manifest["passed"] = True
        return 0
    except Exception as error:
        manifest["error"] = str(error)
        print(str(error), file=sys.stderr)
        return 1
    finally:
        (output / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")


if __name__ == "__main__":
    sys.exit(main())

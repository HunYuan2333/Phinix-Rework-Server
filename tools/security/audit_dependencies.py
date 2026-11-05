#!/usr/bin/env python3
"""Online, fail-closed NuGet audit of the dedicated server graph (including legacy build targets)."""
import argparse
import gzip
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import urllib.request

ROOT = Path(__file__).resolve().parents[2]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True, help='Fresh evidence directory')
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    manifest = {'schema': 1, 'passed': False, 'scope': 'NuGet restore graph, not OS/runtime/vendor certification', 'steps': []}
    try:
        env = dict(os.environ, DirectoryBuildTargetsPath=str(ROOT / 'Directory.Build.targets'))
        sdk = subprocess.check_output(['dotnet', '--list-sdks'], text=True).strip().splitlines()[-1]
        version, base = re.fullmatch(r'(\S+) \[(.+)\]', sdk).groups()
        env['MSBuildSDKsPath'] = str(Path(base) / version / 'Sdks')
        manifest['sdk'] = subprocess.check_output(['dotnet', '--info'], text=True)
        manifest['baseSha'] = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip()
        manifest['submodules'] = subprocess.check_output(['git', 'submodule', 'status', '--recursive'], cwd=ROOT, text=True)
        manifest['sourceHashes'] = {str(p.relative_to(ROOT)): hashlib.sha256(p.read_bytes()).hexdigest()
                                    for p in [ROOT / 'Directory.Build.targets', ROOT / 'Server/Server.csproj',
                                              ROOT / 'Server/packages.lock.json', ROOT / 'Server/ServerStorage/packages.lock.json',
                                              ROOT / 'Server/ServerRuntime/packages.lock.json']}
        url = 'https://api.nuget.org/v3/vulnerabilities/index.json'
        request = urllib.request.Request(url, headers={'Accept-Encoding': 'identity'})
        with urllib.request.urlopen(request, timeout=30) as response:
            raw = response.read()
        if raw[:2] == b'\x1f\x8b':
            raw = gzip.decompress(raw)
        index = json.loads(raw)
        if not isinstance(index, list) or not index or any('@id' not in group or '@updated' not in group for group in index):
            raise ValueError('No valid vulnerability index; audit unavailable')
        (output / 'vulnerability-index.json').write_bytes(raw)
        manifest['vulnerabilitySource'] = {'url': url, 'sha256': hashlib.sha256(raw).hexdigest(), 'groups': index}
        config = output / 'nuget.config'
        config.write_text('<configuration><packageSources><clear /><add key="nuget.org" value="https://api.nuget.org/v3/index.json" /></packageSources>'
                          '<auditSources><clear /><add key="nuget.org" value="https://api.nuget.org/v3/index.json" /></auditSources></configuration>')
        project = str(ROOT / 'Server/Server.csproj')
        commands = [
            ('restore', ['dotnet', 'restore', project, '--configfile', str(config), '--locked-mode', '--no-http-cache', '--force',
                         '-m:1', '-p:NuGetAudit=true', '-p:NuGetAuditMode=all', '-p:NuGetAuditLevel=low',
                         '-p:WarningsAsErrors=NU1900%3BNU1901%3BNU1902%3BNU1903%3BNU1904%3BNU1905']),
            ('inventory', ['dotnet', 'list', project, 'package', '--include-transitive', '--format', 'json', '--no-restore']),
            ('vulnerabilities', ['dotnet', 'list', project, 'package', '--vulnerable', '--include-transitive', '--format', 'json', '--no-restore'])]
        for name, command in commands:
            result = subprocess.run(command, cwd=ROOT, env=env, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=180)
            (output / (name + '.log')).write_text(result.stdout)
            manifest['steps'].append({'name': name, 'command': command, 'exitCode': result.returncode})
            if result.returncode:
                raise RuntimeError(name + ' failed; not a clean audit')
            if name != 'restore':
                report = json.loads(result.stdout)
                if not report.get('projects'):
                    raise ValueError('Missing project evidence')
                (output / (name + '.json')).write_text(json.dumps(report, indent=2) + '\n')
                for entry in report['projects']:
                    for framework in entry.get('frameworks', []):
                        for kind in ('topLevelPackages', 'transitivePackages'):
                            if any(p.get('vulnerabilities') for p in framework.get(kind, [])):
                                raise RuntimeError('Known vulnerable package remains')
        manifest['passed'] = True
        print('SERVER NUGET AUDIT passed; refreshed metadata, all severities, direct/transitive and legacy restore targets')
        return 0
    except Exception as error:
        manifest['error'] = type(error).__name__ + ': ' + str(error)
        print('SERVER NUGET AUDIT failed; see evidence; never treat unavailable data as clean', file=sys.stderr)
        return 1
    finally:
        manifest['outputs'] = {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in output.iterdir() if p.is_file()}
        (output / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')


if __name__ == '__main__':
    raise SystemExit(main())

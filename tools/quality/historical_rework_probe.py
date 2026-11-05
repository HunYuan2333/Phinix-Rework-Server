#!/usr/bin/env python3
"""Fixed historical Rework modules in separate Mono processes against real current auth/user components."""
import argparse
import json
import os
from pathlib import Path
import re
import selectors
import shutil
import signal
import subprocess
import sys
import tempfile
import time

from check import ROOT, digest, repositories, snapshot, supervise
from legacy_probe import archive

CLIENT = '23643e194c254026b3665ced1e618123d68f7566'
COMMON = '9d0710aaa9b41c07f28d9bfa25599247e7bb1f93'
PROTOBUF = '4b0c3aacf0657fbf38253b38918d3358dd4319ec'


def read_ready(stream, timeout):
    deadline = time.monotonic() + timeout
    data = b''
    with selectors.DefaultSelector() as selector:
        selector.register(stream, selectors.EVENT_READ)
        while b'\n' not in data:
            remaining = deadline - time.monotonic()
            if remaining <= 0 or not selector.select(remaining):
                raise RuntimeError('Component peer readiness timeout')
            chunk = os.read(stream.fileno(), 4096)
            if not chunk or len(data) + len(chunk) > 8192:
                raise RuntimeError('Invalid component peer readiness line')
            data += chunk
    return json.loads(data.split(b'\n', 1)[0])


def exchange(host_command, client_command, credentials, capture, work, output, env, readiness_timeout=10):
    """Own both process groups with bounded readiness, client and stop deadlines."""
    records = []
    with tempfile.TemporaryFile() as errors:
        host = subprocess.Popen(host_command, cwd=work, env=env, stdin=subprocess.PIPE,
                                stdout=subprocess.PIPE, stderr=errors, text=True, start_new_session=True)
        try:
            ready = read_ready(host.stdout, readiness_timeout)
            if not ready.get('ready') or not 0 < ready.get('port', 0) < 65536:
                raise RuntimeError('Invalid component peer readiness')
            results = []
            for phase in ['initial', 'new-process-same-credentials']:
                command = [*client_command, str(ready['port']), str(credentials)]
                record, log = supervise(command, work, 20, env)
                record['name'] = phase
                records.append(record)
                (output / (phase + '.txt')).write_text(log)
                if not record['passed']:
                    raise RuntimeError('Historical client failed: ' + phase)
                result = json.loads(log.strip())
                if not result.get('passed') or not result.get('credentialFilePersisted') or not result.get('secretFreePublicSync'):
                    raise RuntimeError('Historical client result missing')
                results.append(result)
            if not results[0]['uuid'] or results[0]['uuid'] != results[1]['uuid']:
                raise RuntimeError('Persistent identity did not survive new client process')
            host.stdin.write('stop\n')
            host.stdin.flush()
            host.wait(timeout=10)
            if host.returncode != 0 or not capture.is_file():
                raise RuntimeError('Component peer/capture failed')
            return {'passed': True, 'hostCommand': host_command, 'clientRuns': records, 'hostExitCode': host.returncode,
                    'newClientProcessRetainedUuid': True, 'results': results,
                    'scope': 'Separate historical client processes, same current component peer; not server restart'}
        finally:
            if host.poll() is None:
                os.killpg(host.pid, signal.SIGKILL)
                host.wait()
            host.stdin.close()
            host.stdout.close()
            errors.seek(0)
            (output / 'peer-stderr.txt').write_text(errors.read().decode(errors='replace'))
            # Preserve failure/timeout command evidence too.
            (output / 'network-commands.json').write_text(json.dumps({'hostCommand': host_command, 'hostExitCode': host.returncode, 'clientRuns': records}, indent=2) + '\n')


def published_exchange(server, writer, client_exe, private, output, env):
    """Real published Main, then a fresh server process; synthetic persistent client identity."""
    server = server.resolve(); writer = writer.resolve()
    record, log = supervise(['dotnet', str(writer), '--write-config', str(private/'server.conf')], private, 20, env)
    (output/'published-config.txt').write_text(log)
    if not record['passed']: raise RuntimeError('Published historical config failed')
    results = []
    for phase in ['initial', 'server-restart']:
        path = output/('published-'+phase+'-server.txt')
        with path.open('w') as log:
            host = subprocess.Popen(['dotnet',str(server)], cwd=private, env=env, stdin=subprocess.PIPE,
                                    stdout=log, stderr=subprocess.STDOUT, text=True)
            try:
                deadline = time.monotonic()+12; port = None
                while time.monotonic()<deadline and host.poll() is None:
                    match = re.search(r'listening on .*:(\d+)',path.read_text(errors='replace'))
                    if match: port=int(match.group(1)); break
                    time.sleep(.05)
                if port is None: raise RuntimeError('Published historical server startup failed')
                command = ['mono',str(client_exe),str(port),str(private/'synthetic-credentials.bin')]
                run, text = supervise(command, private, 20, env)
                (output/('published-'+phase+'-client.txt')).write_text(text)
                if not run['passed']: raise RuntimeError('Published historical client failed: '+phase)
                data=json.loads(text.strip())
                if not data.get('passed') or not data.get('secretFreePublicSync') or not data.get('credentialFilePersisted'):
                    raise RuntimeError('Published historical client missing expected proof')
                results.append(data)
                host.stdin.write('stop\n'); host.stdin.flush(); host.wait(timeout=12)
                if host.returncode: raise RuntimeError('Published historical server stop failed')
            finally:
                if host.poll() is None: host.kill(); host.wait(timeout=5)
                if host.stdin: host.stdin.close()
    if not results[0]['uuid'] or results[0]['uuid']!=results[1]['uuid']:
        raise RuntimeError('Published historical UUID changed across server restart')
    return {'passed':True, 'scope':'Pinned historical auth/login against published Main, real UDP and server restart; no Framework/Trade/game',
            'newServerProcessRetainedUuid':True, 'results':results,
            'serverFiles':{str(f.relative_to(server.parent)):digest(f) for f in server.parent.rglob('*') if f.is_file()}}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--client-source', type=Path, required=True)
    parser.add_argument('--common-source', type=Path, required=True)
    parser.add_argument('--protobuf-source', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--server', type=Path)
    parser.add_argument('--config-writer', type=Path)
    args = parser.parse_args()
    if bool(args.server) != bool(args.config_writer): parser.error('--server and --config-writer must be used together')
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    manifest = {'schema': 1, 'profile': 'HistoricalRework', 'clientSha': CLIENT, 'commonSha': COMMON,
                'protobufSha': PROTOBUF, 'passed': False, 'checks': [], 'seed': None, 'faultIds': [],
                'scope': 'Historical auth/login modules only; not Framework host, negotiation, Trade or game'}
    try:
        sdk = subprocess.check_output(['dotnet', '--list-sdks']).decode().strip().splitlines()[-1]
        version, base = re.fullmatch(r'(\S+) \[(.+)\]', sdk).groups()
        env = dict(os.environ, MSBuildSDKsPath=str(Path(base) / version / 'Sdks'),
                   MSBuildEnableWorkloadResolver='false', DOTNET_CLI_TELEMETRY_OPTOUT='1')
        manifest['sdk'] = subprocess.check_output(['dotnet', '--info']).decode()
        manifest['mono'] = subprocess.check_output(['mono', '--version']).decode()
        manifest['runnerSha256'] = digest(Path(__file__))
        manifest['archiveHelperSha256'] = digest(Path(__file__).with_name('legacy_probe.py'))
        with tempfile.TemporaryDirectory(prefix='phinix-historical-rework-') as directory:
            work = Path(directory)
            historical = work / 'historical'
            archive(args.client_source, CLIENT, ['Client/Common', 'Directory.Build.props'], historical)
            archive(args.common_source, COMMON, ['Common', 'libs'], historical / 'Dependencies/Phinix.Common')
            archive(args.protobuf_source, PROTOBUF, ['csharp'], historical / 'Dependencies/Phinix.Common/Dependencies/protobuf')
            # Verify archived parent gitlinks, not the mutable sibling checkout.
            common_link = subprocess.check_output(['git', '-C', str(args.client_source), 'ls-tree', CLIENT, 'Dependencies/Phinix.Common']).decode()
            proto_link = subprocess.check_output(['git', '-C', str(args.common_source), 'ls-tree', COMMON, 'Dependencies/protobuf']).decode()
            if COMMON not in common_link or PROTOBUF not in proto_link:
                raise RuntimeError('Historical gitlink mismatch')
            manifest['historicalSourceInputs'] = {str(p.relative_to(historical)): digest(p) for p in historical.rglob('*')
                                                  if p.is_file() and p.suffix in {'.cs', '.csproj', '.proto', '.props', '.dll'}}
            current = work / 'current'
            current.mkdir()
            repos = repositories(ROOT)
            manifest['currentRepositories'] = repos
            manifest['currentSourceInputs'] = snapshot(ROOT, current, repos)
            producer = work / 'producer'
            shutil.copytree(ROOT / 'Tests/HistoricalReworkClientProbe', producer, ignore=shutil.ignore_patterns('bin', 'obj'))
            private = work / 'private-runtime'
            private.mkdir()
            env['TMPDIR'] = str(private)

            def step(name, command, cwd=work):
                print(name, flush=True)
                record, log = supervise(command, cwd, 180, env)
                record['name'] = name
                manifest['checks'].append(record)
                (output / (name + '.txt')).write_text(log)
                if not record['passed']:
                    raise RuntimeError(name + ' failed; see build evidence')

            flags = ['-m:1', '-p:BuildInParallel=false', '-p:NuGetAudit=false']
            project = historical / 'Client/Common/UserManagement.Client/UserManagement.Client.csproj'
            step('historical-client-restore', ['dotnet', 'restore', str(project), '--disable-parallel', *flags])
            step('historical-client-build', ['dotnet', 'build', str(project), '-c', 'Release', '--no-restore', *flags])
            step('producer-build', ['dotnet', 'build', str(producer / 'HistoricalReworkClientProbe.csproj'), '-c', 'Release',
                                    '-p:HistoricalClientRoot=' + str(historical), *flags])
            if not args.server:
                host_project = current / 'Tests/HistoricalReworkPeerHost/HistoricalReworkPeerHost.csproj'
                step('peer-build', ['dotnet', 'build', str(host_project), '-c', 'Release', *flags])
            manifest['historicalBinaryHashes'] = {str(p.relative_to(historical)): digest(p) for p in historical.rglob('*.dll') if 'bin' in p.parts}
            manifest['producerBinaryHashes'] = {str(p.relative_to(producer)): digest(p) for p in (producer / 'bin').rglob('*.exe')}
            if args.server:
                manifest['scope']='Pinned historical auth/login against published Main with actual server restart; not Framework/Trade/game'
                manifest['network']=published_exchange(args.server,args.config_writer,producer/'bin/Release/net472/HistoricalReworkClientProbe.exe', private, output, env)
            else:
                host_dll = current / 'Tests/HistoricalReworkPeerHost/bin/Release/net10.0/HistoricalReworkPeerHost.dll'
                client_exe = producer / 'bin/Release/net472/HistoricalReworkClientProbe.exe'
                manifest['network'] = exchange(['dotnet', str(host_dll), str(output / 'capture.json')], ['mono', str(client_exe)],
                                               private / 'credentials.bin', output / 'capture.json', work, output, env)
                capture = json.loads((output / 'capture.json').read_text())
                fixtures = capture['fixtures']
                if len(fixtures) != 2 or fixtures[0]['derivedSha256'] != fixtures[1]['derivedSha256']:
                    raise RuntimeError('Historical capture not stable across processes')
                manifest['fixtures'] = fixtures
            manifest['passed'] = True
            print('HISTORICAL REWORK RESULT passed; realUDP=true; newClientProcessRetainedUuid=true', flush=True)
        return 0
    except Exception as error:
        manifest['error'] = str(error)
        print(str(error), file=sys.stderr)
        return 1
    finally:
        if (output / 'network-commands.json').is_file():
            manifest['networkAttempt'] = json.loads((output / 'network-commands.json').read_text())
        (output / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')


if __name__ == '__main__':
    sys.exit(main())

#!/usr/bin/env python3
"""Real published host load refusal; synthetic isolated snapshots only."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import signal
import subprocess
import tempfile
import time


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def run_host(server, work, log_file, stop_when_ready):
    command = ['dotnet', str(server)]
    with log_file.open('w') as log:
        process = subprocess.Popen(command, cwd=work, stdin=subprocess.PIPE, stdout=log,
                                   stderr=subprocess.STDOUT, start_new_session=True, text=True)
        try:
            deadline = time.monotonic() + 10
            while process.poll() is None:
                if 'listening on' in log_file.read_text():
                    if not stop_when_ready:
                        raise RuntimeError('Invalid persistent state reached listening')
                    process.stdin.write('stop\n')
                    process.stdin.flush()
                    process.wait(timeout=10)
                    break
                if time.monotonic() >= deadline:
                    raise RuntimeError('Host load deadline')
                time.sleep(.05)
            return process.returncode
        finally:
            if process.poll() is None:
                os.killpg(process.pid, signal.SIGKILL)
                process.wait()
            process.stdin.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--server', type=Path, required=True)
    parser.add_argument('--test-tool', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    server = args.server.resolve()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    result = {'schema': 1, 'passed': False, 'seed': None, 'faultIds': [], 'cases': [],
              'command': ['dotnet', str(server)], 'serverSha256': digest(server),
              'scope': 'Linux isolated startup refusal; not transactions/game delivery'}
    marker = 'SYNTHETIC_PRIVATE_PAYLOAD_MUST_NOT_BE_LOGGED'
    try:
        for name in ['trade-corrupt', 'trade-db-corrupt', 'trade-db-missing', 'trade-marker-missing', 'chat-corrupt', 'credentials-corrupt', 'users-corrupt', 'config-corrupt', 'trade-directory']:
            with tempfile.TemporaryDirectory(prefix='phinix-load-smoke-') as directory:
                work = Path(directory)
                config_command = ['dotnet', str(args.test_tool.resolve()), '--write-config', str(work / 'server.conf')]
                subprocess.run(config_command, check=True, capture_output=True, timeout=10)
                seed_log = output / (name + '-seed.txt')
                if run_host(server, work, seed_log, True) != 0 or 'listening on' not in seed_log.read_text():
                    raise RuntimeError('Seed startup/stop failed')
                if name.startswith('trade'):
                    target, = (work / 'framework-extensions').rglob('trade-state.bin')
                    if name.startswith('trade-db-'): target = Path(str(target) + '.sqlite3')
                    if name == 'trade-marker-missing': target = Path(str(target) + '.authority')
                elif name.startswith('chat'):
                    candidates = list((work / 'framework-extensions').rglob('chat-history.bin'))
                    target, = candidates
                else:
                    target = work / {'credentials-corrupt': 'credentials', 'users-corrupt': 'users', 'config-corrupt': 'server.conf'}[name]
                if name.endswith('missing'):
                    target.unlink()
                elif name.endswith('directory'):
                    target.unlink()
                    target.mkdir()
                else:
                    target.write_bytes(b'\xff<invalid-' + marker.encode())
                before = {str(p.relative_to(work)): digest(p) for p in work.rglob('*') if p.is_file() and p.name not in ['host.txt', 'server.log']}
                log_file = output / (name + '.txt')
                code = run_host(server, work, log_file, False)
                text = log_file.read_text()
                if code != 1 or 'Server startup failed:' not in text or 'listening on' in text:
                    raise RuntimeError(f'{name}: startup refusal not explicit (exit {code})')
                if marker in text or marker in (work / 'server.log').read_text():
                    raise RuntimeError('Persistence payload leaked to logs')
                after = {str(p.relative_to(work)): digest(p) for p in work.rglob('*') if p.is_file() and p.name not in ['host.txt', 'server.log']}
                if before != after or (name.endswith('directory') and not target.is_dir()):
                    raise RuntimeError('Failed startup modified persistent snapshots')
                result['cases'].append({'name': name, 'passed': True, 'exitCode': code,
                                        'configCommand': config_command, 'snapshotsBefore': before, 'snapshotsAfter': after})
        result['passed'] = True
        print('HOST LOAD RESULT passed; cases=9; invalid state refused before listening; snapshots unchanged')
        return 0
    except Exception as error:
        result['error'] = str(error)
        print(str(error))
        return 1
    finally:
        (output / 'manifest.json').write_text(json.dumps(result, indent=2) + '\n')


if __name__ == '__main__':
    raise SystemExit(main())

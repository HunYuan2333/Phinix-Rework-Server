#!/usr/bin/env python3
"""Published server + independently built client control-plane probe, isolated state only."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import signal
import subprocess
import tempfile
import time


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('server', 'test-tool', 'client-probe', 'output'):
        parser.add_argument('--' + name, type=Path, required=True)
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    result = {'schema': 1, 'passed': False, 'profile': 'Rework V2 admission-control subset',
              'seed': None, 'faultIds': [], 'scope': 'Independent processes, real UDP/auth/login/control; no game delivery or durable transaction evidence',
              'inputs': {name: {'path': str(path.resolve()), 'sha256': digest(path)}
                         for name, path in [('server', args.server), ('testTool', args.test_tool), ('clientProbe', args.client_probe)]}}
    try:
        with tempfile.TemporaryDirectory(prefix='phinix-admission-') as directory:
            work = Path(directory)
            config_command = ['dotnet', str(args.test_tool.resolve()), '--write-config', str(work / 'server.conf')]
            subprocess.run(config_command, check=True, capture_output=True, timeout=10)
            result['configCommand'] = config_command
            host_command = ['dotnet', str(args.server.resolve())]
            result['serverCommand'] = host_command
            log_path = output / 'server.txt'
            with log_path.open('w') as log:
                host = subprocess.Popen(host_command, cwd=work, stdin=subprocess.PIPE, stdout=log,
                                        stderr=subprocess.STDOUT, text=True, start_new_session=True)
                try:
                    deadline = time.monotonic() + 10
                    port = None
                    while port is None:
                        match = re.search(r'listening on 0\.0\.0\.0:(\d+)', log_path.read_text())
                        if match:
                            port = int(match.group(1))
                            break
                        if host.poll() is not None or time.monotonic() >= deadline:
                            raise RuntimeError('Server startup failed/deadline')
                        time.sleep(.05)
                    command = ['dotnet', str(args.client_probe.resolve()), '--probe', str(port)]
                    result['clientCommand'] = command
                    with (output / 'client.txt').open('w') as client_log:
                        client = subprocess.Popen(command, cwd=work, stdout=client_log, stderr=subprocess.STDOUT,
                                                  start_new_session=True)
                        try:
                            client.wait(timeout=30)
                        finally:
                            if client.poll() is None:
                                os.killpg(client.pid, signal.SIGKILL)
                                client.wait(timeout=5)
                    result['clientExitCode'] = client.returncode
                    if client.returncode != 0 or 'ADMISSION NETWORK CLIENT RESULT passed' not in (output / 'client.txt').read_text():
                        raise RuntimeError('Independent client assertions failed')
                    host.stdin.write('stop\n')
                    host.stdin.flush()
                    host.wait(timeout=10)
                    result['serverExitCode'] = host.returncode
                    if host.returncode != 0:
                        raise RuntimeError('Server shutdown failed')
                finally:
                    if host.poll() is None:
                        os.killpg(host.pid, signal.SIGKILL)
                        host.wait(timeout=5)
                    host.stdin.close()
        result['passed'] = True
        print('ADMISSION PROCESS RESULT passed; published server + client-pinned codecs; cases=2')
        return 0
    except Exception as error:
        result['errorType'] = type(error).__name__
        print('ADMISSION PROCESS RESULT failed; ' + type(error).__name__)
        return 1
    finally:
        result['outputs'] = {p.name: digest(p) for p in output.glob('*.txt')}
        (output / 'manifest.json').write_text(json.dumps(result, indent=2) + '\n')


if __name__ == '__main__':
    raise SystemExit(main())

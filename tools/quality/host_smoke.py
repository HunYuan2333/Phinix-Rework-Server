#!/usr/bin/env python3
"""Linux published host smoke, isolated empty state; no clients/player data/fault hooks."""
import argparse
import json
import os
from pathlib import Path
import signal
import socket
import re
import subprocess
import tempfile
import time


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--server', type=Path, required=True)
    parser.add_argument('--test-tool', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    result = {'schema': 1, 'passed': False, 'seed': None, 'faultIds': [], 'cases': [],
              'scope': 'Linux process lifecycle, empty isolated state; not asset durability/game delivery'}
    try:
        for name in ['eof-sigterm', 'console-stop', 'extension-save-failure']:
            with tempfile.TemporaryDirectory(prefix='phinix-host-smoke-') as directory:
                work = Path(directory)
                command = ['dotnet', str(args.test_tool.resolve()), '--write-config', str(work / 'server.conf')]
                subprocess.run(command, check=True, capture_output=True, timeout=10)
                log_file = work / 'host.txt'
                host_command = ['dotnet', str(args.server.resolve())]
                with log_file.open('w') as log:
                    process = subprocess.Popen(host_command, cwd=work, stdin=subprocess.DEVNULL if name == 'eof-sigterm' else subprocess.PIPE,
                                               stdout=log, stderr=subprocess.STDOUT, start_new_session=True, text=True)
                    try:
                        deadline = time.monotonic() + 10
                        while 'listening on' not in log_file.read_text():
                            if process.poll() is not None or time.monotonic() >= deadline:
                                raise RuntimeError('Host startup failed/deadline')
                            time.sleep(.05)
                        assert process.poll() is None
                        states = list((work / 'framework-extensions').rglob('trade-state.bin'))
                        if len(states) != 1 or not states[0].is_file():
                            raise RuntimeError('Actual built-in persistent extension did not load')
                        chat, = (work / 'framework-extensions').rglob('chat-history.bin')
                        os.utime(chat, (1, 1))
                        sealed_source = states[0].read_bytes()
                        if not Path(str(states[0]) + '.sqlite3').is_file():
                            raise RuntimeError('Actual Trade journal did not initialize')
                        cpu_ticks = None
                        if name == 'eof-sigterm':
                            stat = Path(f'/proc/{process.pid}/stat')
                            before = sum(map(int, stat.read_text().split()[13:15]))
                            time.sleep(.5)
                            assert process.poll() is None
                            cpu_ticks = sum(map(int, stat.read_text().split()[13:15])) - before
                            # Record idle observation; do not invent a production CPU/latency budget.
                            process.send_signal(signal.SIGTERM)
                        else:
                            if name == 'extension-save-failure':
                                states[0].unlink()
                                states[0].mkdir()  # Deterministic OS write error, not a client-triggerable hook.
                            process.stdin.write('stop\n')
                            process.stdin.flush()
                        process.wait(timeout=10)
                        text = log_file.read_text()
                        expected = 1 if name == 'extension-save-failure' else 0
                        if process.returncode != expected:
                            raise RuntimeError(f'{name}: unexpected exit code {process.returncode}')
                        if name == 'extension-save-failure' and 'Server shutdown failed: IOException' not in text:
                            raise RuntimeError('Extension save failure not propagated')
                        if name != 'extension-save-failure':
                            if chat.stat().st_mtime_ns <= 1000000000:
                                raise RuntimeError('Final extension save was skipped after shutdown')
                            if states[0].read_bytes() != sealed_source:
                                raise RuntimeError('Trade import source was rewritten after cutover')
                        if not all((work / f).is_file() for f in ['server.conf', 'credentials', 'users']):
                            raise RuntimeError('Expected isolated snapshots absent')
                        result['cases'].append({'name': name, 'passed': True, 'command': host_command,
                                                'configCommand': command, 'exitCode': process.returncode,
                                                'idleCpuTicksOverHalfSecond': cpu_ticks, 'clockTicksPerSecond': os.sysconf('SC_CLK_TCK')})
                    finally:
                        if process.poll() is None:
                            os.killpg(process.pid, signal.SIGKILL)
                            process.wait()
                        if process.stdin: process.stdin.close()
                        (output / (name + '.txt')).write_text(log_file.read_text())
        with tempfile.TemporaryDirectory(prefix='phinix-host-bind-failure-') as directory, socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as occupied:
            work = Path(directory)
            occupied.bind(('0.0.0.0', 0))
            config = work / 'server.conf'
            command = ['dotnet', str(args.test_tool.resolve()), '--write-config', str(config)]
            subprocess.run(command, check=True, capture_output=True, timeout=10)
            content, count = re.subn(r'<Port>0</Port>', f'<Port>{occupied.getsockname()[1]}</Port>', config.read_text())
            if count != 1:
                raise RuntimeError('Could not set the occupied test port')
            config.write_text(content)
            host_command = ['dotnet', str(args.server.resolve())]
            failed = subprocess.run(host_command, cwd=work, stdin=subprocess.DEVNULL,
                                    stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, timeout=10)
            (output / 'occupied-port.txt').write_text(failed.stdout)
            if (failed.returncode != 1 or 'Server startup failed: IOException' not in failed.stdout
                    or 'listening on' in failed.stdout or 'Server cleanup failed:' in failed.stdout):
                raise RuntimeError('Occupied port did not produce a clean startup abort')
            # Load initializes missing stores. A final save would write each a second time.
            if config.read_text() != content or any(failed.stdout.count(marker) != 1 for marker in
                    ('Saved 0 credentials', 'Saved 0 users', 'Saved 0 chat messages')):
                raise RuntimeError('Failed startup ran the normal final save')
            result['cases'].append({'name': 'occupied-port', 'passed': True, 'command': host_command,
                                    'configCommand': command, 'exitCode': failed.returncode})
        result['passed'] = True
        print('HOST SMOKE RESULT passed; cases=4; EOF/SIGTERM/console/save-failure/occupied-port')
        return 0
    except Exception as error:
        result['error'] = str(error)
        print(str(error))
        return 1
    finally:
        (output / 'manifest.json').write_text(json.dumps(result, indent=2) + '\n')


if __name__ == '__main__':
    raise SystemExit(main())

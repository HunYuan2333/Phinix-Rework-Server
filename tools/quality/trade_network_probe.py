#!/usr/bin/env python3
"""Two independently compiled clients, published host kill/restart, literal persistent-state oracle."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import signal
import sqlite3
import subprocess
import tempfile
import time
import xml.etree.ElementTree as ET
from check import supervise


def digest(p): return hashlib.sha256(p.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('server', 'test-tool', 'client-probe', 'output'):
        parser.add_argument('--' + name, type=Path, required=True)
    args = parser.parse_args()
    output = args.output.resolve(); output.mkdir(parents=True, exist_ok=False)
    result = {'schema': 1, 'passed': False, 'profile': 'client-pinned Framework auth/login/Trade subset',
              'seed': None, 'faultIds': ['trade.server.kill-after-offer-response', 'identity.snapshot-save.path-directory'],
              'inputs': {name: {'path': str(p.resolve()), 'sha256': digest(p)} for name, p in [('server', args.server), ('tool', args.test_tool), ('client', args.client_probe)]}, 'phases': []}
    try:
        adapter_fixture = os.environ.get('PHINIX_TEST_ADAPTER_WIRE')
        if adapter_fixture:
            result['adapterFixture'] = {name: {'path': str((Path(adapter_fixture) / name).resolve()),
                                               'sha256': digest(Path(adapter_fixture) / name)} for name in ('item.bin', 'offer.bin')}
        with tempfile.TemporaryDirectory(prefix='phinix-trade-wire-') as directory:
            work = Path(directory)
            config = ['dotnet', str(args.test_tool.resolve()), '--write-config', str(work / 'server.conf')]
            subprocess.run(config, check=True, capture_output=True, timeout=10)
            result['configCommand'] = config
            state_path = work / 'synthetic-clients.private.json'  # Ephemeral keys, never copied to results.
            for phase in ('initial', 'restart', 'login-failure'):
                log_path = output / (phase + '-server.txt')
                with log_path.open('w') as log:
                    command = ['dotnet', str(args.server.resolve())]
                    host = subprocess.Popen(command, cwd=work, stdin=subprocess.PIPE, stdout=log, stderr=subprocess.STDOUT, start_new_session=True, text=True)
                    saved_credentials = None
                    try:
                        deadline = time.monotonic() + 10
                        while True:
                            match = re.search(r'listening on 0\.0\.0\.0:(\d+)', log_path.read_text())
                            if match: break
                            if host.poll() is not None or time.monotonic() >= deadline: raise RuntimeError('Host startup failed/deadline')
                            time.sleep(.05)
                        if phase == 'login-failure':
                            saved_credentials = work / 'credentials.saved'
                            (work / 'credentials').rename(saved_credentials); (work / 'credentials').mkdir()
                        probe = ['dotnet', str(args.client_probe.resolve()), '--trade-probe', match.group(1), phase, str(state_path)]
                        record, text = supervise(probe, work, 35)
                        (output / (phase + '-client.txt')).write_text(text)
                        result['phases'].append({'phase': phase, 'serverCommand': command, 'client': record})
                        if not record['passed'] or 'TRADE WIRE RESULT passed;' not in text: raise RuntimeError('Client protocol assertion failed')
                        if phase != 'login-failure':
                            state = json.loads(state_path.read_text())
                            database, = (work / 'framework-extensions').rglob('trade-state.bin.sqlite3')
                            with sqlite3.connect(database.as_uri() + '?mode=ro', uri=True) as db:
                                assert db.execute('PRAGMA integrity_check').fetchone() == ('ok',)
                                raw = db.execute("SELECT value FROM state WHERE id='trade-ledger-v1'").fetchone()[0]
                                root = ET.fromstring(raw)
                                values = {e.tag.rsplit('}', 1)[-1]: e for e in root}
                                active = list(values['ActiveTrades']); assert len(active) == 1
                                fields = {e.tag.rsplit('}', 1)[-1]: e for e in active[0]}
                                assert fields['TradeId'].text == state['Trade'] and fields['SnapshotVersion'].text == '2'
                                assert len(values['PendingCompletionNotifications']) == 0
                                assert db.execute("SELECT count(*) FROM operations WHERE principal=? AND operation=?", (state['Principals'][0], state.get('AdapterOperation') or 'wire-offer')).fetchone() == (1,)
                            # Neither synthetic keys nor auth sessions are emitted as evidence/artifacts.
                            assert all(key not in log_path.read_text() for key in state['Keys'])
                        if phase == 'initial':
                            os.killpg(host.pid, signal.SIGKILL); host.wait(timeout=5)
                            result['phases'][-1]['termination'] = 'SIGKILL after offer response, before timer/final save'
                        else:
                            if saved_credentials:
                                (work / 'credentials').rmdir(); saved_credentials.rename(work / 'credentials'); saved_credentials = None
                            host.stdin.write('stop\n'); host.stdin.flush(); host.wait(timeout=10)
                            if host.returncode != 0: raise RuntimeError('Host stop failed')
                        result['phases'][-1]['serverExitCode'] = host.returncode
                    finally:
                        if host.poll() is None: os.killpg(host.pid, signal.SIGKILL); host.wait(timeout=5)
                        host.stdin.close()
                        if saved_credentials and saved_credentials.exists():
                            if (work / 'credentials').is_dir(): (work / 'credentials').rmdir()
                            saved_credentials.rename(work / 'credentials')
        result['passed'] = True
        print('TRADE PROCESS RESULT passed; clients=2; initial/kill/restart/replay/identity-save-refusal; no game inventory or ACK claim')
        return 0
    except Exception as error:
        result['errorType'] = type(error).__name__
        print('TRADE PROCESS RESULT failed; ' + type(error).__name__)
        return 1
    finally:
        result['outputs'] = {p.name: digest(p) for p in output.glob('*.txt')}
        (output / 'manifest.json').write_text(json.dumps(result, indent=2) + '\n')


if __name__ == '__main__': raise SystemExit(main())

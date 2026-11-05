#!/usr/bin/env python3
"""Published server + independent protocol subprocess, private synthetic identities and SQL oracle."""
import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import re
import signal
import sqlite3
import subprocess
import sys
import tempfile
import time
import xml.etree.ElementTree as ET


def local(element): return element.tag.rsplit('}', 1)[-1]
def fields(element): return {local(e): e for e in element}
def digest(path): return hashlib.sha256(path.read_bytes()).hexdigest()


def run_client(command, cwd, timeout):
    # Inherit the driver's process group: an outer supervisor timeout must kill
    # the host AND peer, not strand a nested group. This peer spawns no children.
    started = time.monotonic()
    with tempfile.TemporaryFile() as log:
        client = subprocess.Popen(command, cwd=cwd, stdout=log, stderr=subprocess.STDOUT)
        timed_out = False
        try:
            client.wait(timeout=timeout)
        except subprocess.TimeoutExpired:
            timed_out = True; client.kill(); client.wait()
        except BaseException:
            client.kill(); client.wait(); raise
        log.seek(0); text = log.read().decode(errors='replace')
    return {'command': command, 'exitCode': client.returncode, 'timedOut': timed_out,
            'seconds': round(time.monotonic() - started, 3), 'passed': not timed_out and client.returncode == 0}, text


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--server', type=Path, required=True)
    parser.add_argument('--probe', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    output = args.output.resolve(); output.mkdir(parents=True, exist_ok=False)
    result = {'schema': 1, 'passed': False, 'profile': 'Framework v2 synthetic peer + query capabilities; not game client',
              'seed': None, 'faultIds': ['p3.kill-after-offer-response', 'p3.kill-with-offline-pending', 'config.trade.malformed-section'],
              'inputs': {name: {'path': str(path.resolve()), 'sha256': digest(path)} for name, path in [('server', args.server), ('probe', args.probe)]}, 'phases': []}
    try:
        with tempfile.TemporaryDirectory(prefix='phinix-p3-joint-') as directory:
            work = Path(directory); state_path = work / 'synthetic-private.json'
            subprocess.run(['dotnet', str(args.probe.resolve()), '--write-config', str(work / 'base.conf')], check=True, capture_output=True, timeout=10)
            configure = Path(__file__).resolve().parents[1] / 'configure_trade_limits.py'
            subprocess.run([sys.executable, str(configure), '--input', str(work / 'base.conf'), '--output', str(work / 'server.conf'), '--retained-trades', '1', '--payload-bytes', '16', '--auxiliary-bytes', '1024', '--page-size', '1'], check=True, capture_output=True, timeout=10)
            for phase in ['initial', 'restart', 'policy', 'recovered', 'bad-config']:
                if phase == 'policy':
                    subprocess.run([sys.executable, str(configure), '--input', str(work / 'server.conf'), '--output', str(work / 'next.conf'), '--retained-trades', '0', '--payload-bytes', '0', '--auxiliary-bytes', '0'], check=True, capture_output=True, timeout=10)
                    os.replace(work / 'next.conf', work / 'server.conf')
                if phase == 'bad-config':
                    config = ET.parse(work / 'server.conf')
                    for entry in config.getroot().iter():
                        if any(local(e) == 'Key' and e.text == 'builtin.trade' for e in entry):
                            value = next(e for e in entry if local(e) == 'Value')
                            section = ET.fromstring(value.text)
                            next(e for e in section if local(e) == 'PrincipalPayloadByteLimit').text = 'invalid-synthetic-secret'
                            value.text = ET.tostring(section, encoding='unicode')
                    config.write(work / 'server.conf', encoding='utf-8', xml_declaration=True)
                log_path = output / (phase + '-server.txt')
                with log_path.open('w') as log:
                    host = subprocess.Popen(['dotnet', str(args.server.resolve())], cwd=work, stdin=subprocess.PIPE, stdout=log, stderr=subprocess.STDOUT, text=True)
                    try:
                        deadline = time.monotonic() + 12
                        while True:
                            match = re.search(r'listening on 0\.0\.0\.0:(\d+)', log_path.read_text())
                            if match: break
                            if host.poll() is not None or time.monotonic() > deadline: raise RuntimeError('Server startup/deadline')
                            time.sleep(.05)
                        command = ['dotnet', str(args.probe.resolve()), '--p3-wire', match.group(1), phase, str(state_path)]
                        record, text = run_client(command, work, 45)
                        (output / (phase + '-client.txt')).write_text(text)
                        result['phases'].append({'phase': phase, 'client': record})
                        if not record['passed'] or 'P3 WIRE RESULT passed;' not in text: raise RuntimeError('Protocol assertions failed')
                        state = json.loads(state_path.read_text())
                        database, = (work / 'framework-extensions').rglob('trade-state.bin.sqlite3')
                        with sqlite3.connect(database.as_uri() + '?mode=ro', uri=True) as db:
                            assert db.execute('PRAGMA integrity_check').fetchone() == ('ok',)
                            ledger = fields(ET.fromstring(db.execute("SELECT value FROM state WHERE id='trade-ledger-v1'").fetchone()[0]))
                            assert db.execute("SELECT count(*) FROM operations WHERE operation LIKE 'query-%'").fetchone() == (0,)
                            if phase == 'initial':
                                assert len(ledger['ActiveTrades']) == 1 and len(ledger['TerminalTrades']) == 0 and len(ledger['PendingCompletionNotifications']) == 0
                                assert fields(ledger['ActiveTrades'][0])['SnapshotVersion'].text == '2'
                            else:
                                assert len(ledger['TerminalTrades']) == 1
                                terminal = fields(ledger['TerminalTrades'][0]); assert terminal['TradeId'].text == state['Trade']
                                payloads = [e for e in ledger['TerminalTrades'].iter() if local(e) == 'PayloadBytes']
                                assert len(payloads) == 1 and base64.b64decode(payloads[0].text) == bytes([17, 0, 255])
                                pending = [fields(e) for e in ledger['PendingCompletionNotifications']]
                                assert len(pending) == 2 and {e['RecipientUuid'].text for e in pending} == set(state['Principals'])
                                assert len({e['DeliveryId'].text for e in pending}) == 2
                                own = next(e for e in pending if e['RecipientUuid'].text == state['Principals'][0])
                                other = next(e for e in pending if e['RecipientUuid'].text == state['Principals'][1])
                                assert own['DeliveryState'].text == 'OutcomeUnknown' and own['AckObserved'].text == 'true'
                                assert other['DeliveryState'].text == ('Pending' if phase == 'restart' else 'OutcomeUnknown')
                                original = ET.fromstring(db.execute("SELECT result FROM operations WHERE principal=? AND operation='wire-denied'", (state['Principals'][0],)).fetchone()[0])
                                assert fields(original)['Success'].text == 'false'
                                assert len(ledger['ActiveTrades']) == (0 if phase == 'restart' else 1)
                                if phase != 'restart': assert fields(ledger['ActiveTrades'][0])['TradeId'].text == state['NewTrade']
                            for operation in ['wire-create', 'wire-offer'] + ([] if phase == 'initial' else ['wire-cancel', 'wire-denied']):
                                assert db.execute('SELECT count(*) FROM operations WHERE principal=? AND operation=?', (state['Principals'][0], operation)).fetchone() == (1,)
                        assert all(key not in log_path.read_text() and key not in text for key in state['Keys'])
                        assert 'invalid-synthetic-secret' not in log_path.read_text()
                        if phase in ['initial', 'restart']:
                            host.kill(); host.wait(timeout=5)
                            result['phases'][-1]['termination'] = 'SIGKILL after committed response; no timer/final save'
                        else:
                            host.stdin.write('stop\n'); host.stdin.flush(); host.wait(timeout=10)
                            if host.returncode != 0: raise RuntimeError('Server stop failed')
                        result['phases'][-1]['serverExitCode'] = host.returncode
                    finally:
                        if host.poll() is None: host.kill(); host.wait(timeout=5)
                        host.stdin.close()
        result['passed'] = True
        print('P3 PROCESS RESULT passed; query/recipient/fingerprint/config/replay/offline-pending/two-kills/recovery/corrupt-config-refusal; no game save receipt')
        return 0
    except Exception as error:
        result['errorType'] = type(error).__name__
        print('P3 PROCESS RESULT failed; ' + type(error).__name__)
        return 1
    finally:
        result['outputs'] = {p.name: digest(p) for p in output.glob('*.txt')}
        (output / 'manifest.json').write_text(json.dumps(result, indent=2) + '\n')


if __name__ == '__main__': raise SystemExit(main())

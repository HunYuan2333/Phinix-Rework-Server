#!/usr/bin/env python3
"""Real Trade service/SQLite checks plus independent literal XML/SQL oracle."""
import hashlib
from pathlib import Path
import sqlite3
import sys
import tempfile
import xml.etree.ElementTree as ET
from check import supervise


def local(element):
    return element.tag.rsplit('}', 1)[-1]


def main():
    with tempfile.TemporaryDirectory(prefix='phinix-trade-oracle-') as directory:
        database = Path(directory) / 'trade.sqlite3'
        result, log = supervise(['dotnet', str(Path(sys.argv[1]).resolve()), '--oracle', str(database)], Path.cwd(), 45)
        print(log, end='')
        if not result['passed'] or 'TRADE DURABLE RESULT passed;' not in log:
            return 1
        with sqlite3.connect(database.as_uri() + '?mode=ro', uri=True) as connection:
            assert connection.execute('PRAGMA integrity_check').fetchone() == ('ok',)
            state = connection.execute("SELECT value FROM state WHERE id='trade-ledger-v1'").fetchone()[0]
            root = ET.fromstring(state)
            collections = {local(e): e for e in root}
            assert len(collections['ActiveTrades']) == 0
            terminal = list(collections['TerminalTrades'])
            assert len(terminal) == 1
            participants = next(e for e in terminal[0] if local(e) == 'Participants')
            assert len(participants) == 2
            assert sorted(next(c.text for c in p if local(c) == 'Uuid') for p in participants) == ['a', 'b']
            assert all(next(c.text for c in p if local(c) == 'Accepted') == 'true' for p in participants)
            pending = [{local(c): c.text for c in p} for p in collections['PendingCompletionNotifications']]
            assert len(pending) == 2 and {p['RecipientUuid'] for p in pending} == {'a', 'b'}
            assert all(p['DeliveryState'] == 'OutcomeUnknown' and p['AttemptId'] and p['DeliveryId'] for p in pending)
            assert len({p['DeliveryId'] for p in pending}) == 2
            assert next(p for p in pending if p['RecipientUuid'] == 'b')['AckObserved'] == 'true'
            assert connection.execute("SELECT count(*) FROM operations WHERE operation='offer-fail'").fetchone() == (0,)
            assert connection.execute("SELECT count(*) FROM operations WHERE operation LIKE 'attempt-%'").fetchone() == (2,)
            assert connection.execute("SELECT length(value) FROM state WHERE id='source-hash'").fetchone() == (hashlib.sha256().digest_size,)
            for principal, operation in [('a', 'create-1'), ('a', 'offer-1'), ('a', 'accept-a'), ('b', 'accept-b'), ('b', 'ack-b')]:
                assert connection.execute('SELECT count(*) FROM operations WHERE principal=? AND operation=?', (principal, operation)).fetchone() == (1,)
            raw = state.decode('utf-8')
            assert 'session-a' not in raw and 'session-b' not in raw and 'connection-a' not in raw
        print('TRADE ORACLE RESULT passed; independent SQL/XML terminal/recipient/unknown/ACK/result evidence; not game conservation')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())

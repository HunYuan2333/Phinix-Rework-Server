#!/usr/bin/env python3
"""Run product journal checks, then independently read the synthetic backup using Python SQLite."""
import hashlib
from pathlib import Path
import sqlite3
import sys
import tempfile
from check import supervise


def main():
    if len(sys.argv) != 2:
        raise SystemExit('usage: journal_check.py <ServerJournalTests.dll>')
    with tempfile.TemporaryDirectory(prefix='phinix-sqlite-oracle-') as directory:
        database = Path(directory) / 'oracle.sqlite3'
        record, log = supervise(['dotnet', str(Path(sys.argv[1]).resolve()), '--oracle', str(database)], Path.cwd(), 40)
        print(log, end='')
        if not record['passed'] or 'JOURNAL RESULT passed;' not in log:
            return 1
        with sqlite3.connect(database.as_uri() + '?mode=ro', uri=True) as connection:
            assert connection.execute('PRAGMA integrity_check').fetchone() == ('ok',)
            assert connection.execute('PRAGMA application_id').fetchone() == (0x50484e58,)
            assert connection.execute('PRAGMA user_version').fetchone() == (2,)
            assert connection.execute('SELECT id,value FROM state').fetchall() == [('trade-1', b'accepted-both')]
            assert connection.execute('SELECT delivery,recipient,payload,status FROM outbox').fetchall() == [('delivery-B', 'synthetic-B', b'asset-bytes', 'Pending')]
            rows = connection.execute('SELECT principal,operation,fingerprint,result FROM operations ORDER BY principal,operation').fetchall()
            expected = [('synthetic-A', 'after-busy', b'x', b'ok'), ('synthetic-A', 'held', b'held', b'held'),
                        ('synthetic-A', 'op-1', b'intent-v1', b'committed-result'), ('synthetic-B', 'op-1', b'other-principal', b'separate')]
            assert rows == [(p, op, hashlib.sha256(request).digest(), result) for p, op, request, result in expected]
        print('JOURNAL ORACLE RESULT passed; independent Python SQLite/literal state-result-outbox; synthetic only')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())

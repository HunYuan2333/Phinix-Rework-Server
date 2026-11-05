"""Probe orchestration failures must clean up the external process, never mask failure."""
import os
from pathlib import Path
import sys
import tempfile
import unittest

from historical_rework_probe import exchange


class HistoricalSupervisorTests(unittest.TestCase):
    def assert_stopped(self, pid_file):
        pid = int(pid_file.read_text())
        state = Path(f'/proc/{pid}/stat')
        self.assertTrue(not state.exists() or state.read_text().split()[2] == 'Z')

    def run_failure(self, partial=False):
        with tempfile.TemporaryDirectory() as directory:
            work = Path(directory)
            pid = work / 'host.pid'
            host = work / 'host.py'
            host.write_text('import os,sys,time\n'
                            f'open({str(pid)!r},"w").write(str(os.getpid()))\n' +
                            ('sys.stdout.write("{\\\"ready\\\":");sys.stdout.flush()\n' if partial else
                             'print("{\\\"ready\\\":true,\\\"port\\\":12345}",flush=True)\n') +
                            'time.sleep(30)\n')
            with self.assertRaises(RuntimeError):
                exchange([sys.executable, str(host)], [sys.executable, '-c', 'raise SystemExit(7)'],
                         work / 'credentials', work / 'capture.json', work, work, dict(os.environ), readiness_timeout=.2)
            self.assert_stopped(pid)
            self.assertTrue((work / 'network-commands.json').is_file())

    def test_client_failure_kills_component_peer(self):
        self.run_failure()

    def test_partial_readiness_line_cannot_block_deadline(self):
        self.run_failure(partial=True)


if __name__ == '__main__':
    unittest.main()

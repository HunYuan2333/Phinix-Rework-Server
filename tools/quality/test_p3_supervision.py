"""The outer stage deadline must also stop an in-flight P3 protocol peer."""
import os
from pathlib import Path
import signal
import sys
import tempfile
import unittest
from unittest.mock import Mock

from check import supervise


def process_alive(status):
    # /proc can disappear between existence testing and opening the file: that is exit evidence.
    try:
        text = status.read_text()
    except FileNotFoundError:
        return False
    return text[text.rfind(')') + 2:].split()[0] != 'Z'


class P3SupervisionTests(unittest.TestCase):
    def test_disappearing_process_and_zombie_are_exited(self):
        missing = Mock(); missing.read_text.side_effect = FileNotFoundError()
        self.assertFalse(process_alive(missing))
        status = Mock(); status.read_text.return_value = '1 (name with spaces) Z 0 0'
        self.assertFalse(process_alive(status))
        status.read_text.return_value = '1 (name with spaces) S 0 0'
        self.assertTrue(process_alive(status))

    def test_outer_timeout_owns_peer_process_group(self):
        with tempfile.TemporaryDirectory(prefix='phinix-p3-supervisor-') as directory:
            work = Path(directory); marker = work / 'peer.pid'
            peer = work / 'peer.py'
            peer.write_text('import os,time\n' + f'open({str(marker)!r},"w").write(str(os.getpid()))\n' + 'time.sleep(30)\n')
            wrapper = work / 'wrapper.py'
            wrapper.write_text('import sys\n' + f'sys.path.insert(0,{str(Path(__file__).resolve().parent)!r})\n' +
                               'from p3_acceptance_probe import run_client\n' +
                               f'run_client([{sys.executable!r},{str(peer)!r}],{str(work)!r},30)\n')
            result, _ = supervise([sys.executable, str(wrapper)], work, 2)
            self.assertTrue(result['timedOut']); self.assertFalse(result['passed'])
            self.assertTrue(marker.exists(), 'Peer never started; not a cleanup proof')
            pid = int(marker.read_text()); status = Path(f'/proc/{pid}/stat')
            alive = process_alive(status)
            if alive:
                os.kill(pid, signal.SIGKILL)
            self.assertFalse(alive, 'Outer timeout stranded the peer')


if __name__ == '__main__': unittest.main()

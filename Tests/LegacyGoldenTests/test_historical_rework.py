"""Fixed historical client's actual SendLogin wire; independent .proto field oracle."""
import base64
import copy
import hashlib
import json
from pathlib import Path
import unittest

from test_original_golden import fields

FIXTURES = Path(__file__).resolve().parents[1] / 'Fixtures/HistoricalReworkLogin/normalized.json'


def validate(capture):
    assert capture['profile'] == 'HistoricalRework'
    assert capture['clientSha'] == '23643e194c254026b3665ced1e618123d68f7566'
    assert capture['commonSha'] == '9d0710aaa9b41c07f28d9bfa25599247e7bb1f93'
    assert capture['protobufSha'] == '4b0c3aacf0657fbf38253b38918d3358dd4319ec'
    assert len(capture['fixtures']) == 2
    hashes = []
    for fixture in capture['fixtures']:
        data = base64.b64decode(fixture['derivedAnyBase64'], validate=True)
        assert hashlib.sha256(data).hexdigest() == fixture['derivedSha256']
        envelope = fields(data)
        assert set(envelope) == {1, 2}
        assert envelope[1] == b'Phinix/UserManagement.LoginPacket'
        # Historical SendLogin sets fields 1/2/3, with false field 2 omitted.
        # After removing the session, only DisplayName=3 remains. Do not invent field 4.
        assert fields(envelope[2]) == {3: b'synthetic-historical-rework'}
        assert fixture['module'] == 'UserManagement'
        assert fixture['direction'] == 'historical-client-to-current-component-peer'
        assert fixture['typeUrl'] == envelope[1].decode()
        assert fixture['classification'] == 'redacted-derived-from-fixed-historical-client-process'
        hashes.append(fixture['derivedSha256'])
    assert hashes[0] == hashes[1]


class HistoricalReworkGoldenTests(unittest.TestCase):
    def setUp(self):
        self.capture = json.loads(FIXTURES.read_text())

    def test_real_fixed_client_login_wire_is_stable_across_processes(self):
        validate(self.capture)

    def test_invented_accepting_trades_default_is_rejected(self):
        changed = copy.deepcopy(self.capture)
        fixture = changed['fixtures'][0]
        envelope = fields(base64.b64decode(fixture['derivedAnyBase64']))
        payload = envelope[2] + b'\x20\x01'  # AcceptingTrades=4 true does not exist in the observed request.
        self.assertLess(len(payload), 128)
        data = b'\x0a' + bytes([len(envelope[1])]) + envelope[1] + b'\x12' + bytes([len(payload)]) + payload
        fixture['derivedAnyBase64'] = base64.b64encode(data).decode()
        fixture['derivedSha256'] = hashlib.sha256(data).hexdigest()
        with self.assertRaises(AssertionError):
            validate(changed)

    def test_current_candidate_cannot_replace_fixed_historical_source(self):
        changed = copy.deepcopy(self.capture)
        changed['clientSha'] = '5366dc7e2b6de0d50a087828324c1c88e94fd223'
        with self.assertRaises(AssertionError):
            validate(changed)


if __name__ == '__main__':
    unittest.main()

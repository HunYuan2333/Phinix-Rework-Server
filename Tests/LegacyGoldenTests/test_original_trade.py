"""Original trade oracle: exact field numbers from the pinned .proto, no Server adapter."""
import base64
import copy
import hashlib
import json
from pathlib import Path
import unittest

from test_original_golden import fields

FIXTURES = Path(__file__).resolve().parents[1] / "Fixtures/OriginalTrade/normalized.json"
REQUEST = "Phinix/Trading.UpdateTradeItemsPacket"
RESPONSE = "Phinix/Trading.UpdateTradeItemsResponsePacket"
STEEL = b"\x0a\x05Steel\x10\x07\x28\x3c"
WOOD = b"\x0a\x07WoodLog\x10\x03\x28\x19"


def validate(capture):
    assert capture["sourceSha"] == "ce82b8f2cd57718f05de86458eebd3320f35c4d8"
    assert capture["protobufSha"] == "dd06cc6c8f906b760ef224a085f45e7694dc426a"
    assert capture["profile"] == "Original"
    expected = {}
    # Each row is sender's new offer and receiver's retained own offer.
    for name, sender, receiver, offered, retained in [
        ("A-offer", "A", "B", STEEL, None),
        ("B-offer", "B", "A", WOOD, STEEL),
        ("A-clear", "A", "B", None, WOOD),
    ]:
        request = {2: b"SENDER", 3: b"TRADE", 6: name.encode()}
        result = {1: b"TRADE", 2: name.encode(), 3: 1}
        snapshot = {3: b"TRADE"}
        if offered:
            request[4] = offered
            result[6] = offered
            snapshot[5] = offered
        if retained:
            snapshot[4] = retained
        expected[(name, "client-to-original-server", "sender")] = (REQUEST, request)
        expected[(name, "original-server-to-client", sender)] = (RESPONSE, result)
        expected[(name, "original-server-to-client", receiver)] = (REQUEST, snapshot)
    assert len(capture["fixtures"]) == len(expected)
    seen = set()
    for fixture in capture["fixtures"]:
        key = (fixture["scenario"], fixture["direction"], fixture["peer"])
        assert key not in seen
        seen.add(key)
        type_url, payload = expected[key]
        data = base64.b64decode(fixture["derivedAnyBase64"], validate=True)
        assert hashlib.sha256(data).hexdigest() == fixture["derivedSha256"]
        envelope = fields(data)
        assert set(envelope) == {1, 2}
        assert envelope[1].decode() == fixture["typeUrl"] == type_url
        assert fields(envelope[2]) == payload
        assert fixture["module"] == "Trading"
        assert fixture["classification"] == "redacted-derived-from-original-module-loopback-capture"
    assert seen == set(expected)


class OriginalTradeTests(unittest.TestCase):
    def setUp(self):
        self.capture = json.loads(FIXTURES.read_text())

    def test_three_real_request_result_snapshot_sequences(self):
        validate(self.capture)

    def test_offer_direction_swap_is_detected_even_with_correct_hash(self):
        changed = copy.deepcopy(self.capture)
        fixture = next(f for f in changed["fixtures"] if f["scenario"] == "A-offer" and f["peer"] == "B")
        data = base64.b64decode(fixture["derivedAnyBase64"])
        # OtherPartyItems=5 -> Items=4: same bytes/length, wrong recipient ownership.
        wrong = data.replace(b"\x2a" + bytes([len(STEEL)]) + STEEL,
                             b"\x22" + bytes([len(STEEL)]) + STEEL)
        self.assertNotEqual(data, wrong)
        fixture["derivedAnyBase64"] = base64.b64encode(wrong).decode()
        fixture["derivedSha256"] = hashlib.sha256(wrong).hexdigest()
        with self.assertRaises(AssertionError):
            validate(changed)

    def test_snapshot_cannot_be_substituted_for_operation_result(self):
        changed = copy.deepcopy(self.capture)
        result = next(f for f in changed["fixtures"] if f["scenario"] == "A-clear" and f["peer"] == "A")
        snapshot = next(f for f in changed["fixtures"] if f["scenario"] == "A-clear" and f["peer"] == "B")
        for field in ["derivedAnyBase64", "derivedSha256", "typeUrl"]:
            result[field] = snapshot[field]
        with self.assertRaises(AssertionError):
            validate(changed)

    def test_missing_empty_offer_result_is_detected(self):
        changed = copy.deepcopy(self.capture)
        changed["fixtures"] = [f for f in changed["fixtures"] if not (f["scenario"] == "A-clear" and f["peer"] == "A")]
        with self.assertRaises(AssertionError):
            validate(changed)


if __name__ == "__main__":
    unittest.main()

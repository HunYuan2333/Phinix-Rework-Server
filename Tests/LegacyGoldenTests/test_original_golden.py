"""Independent fixture oracle from pinned original .proto field numbers, not a Server adapter."""
import base64
import hashlib
import json
from pathlib import Path
import unittest

FIXTURES = Path(__file__).resolve().parents[1] / "Fixtures/OriginalLoginChat/normalized.json"


def fields(data):
    result = {}
    position = 0

    def varint():
        nonlocal position
        value = 0
        for shift in range(0, 70, 7):
            if position >= len(data):
                raise ValueError("Truncated synthetic fixture")
            byte = data[position]
            position += 1
            value |= (byte & 127) << shift
            if byte < 128:
                return value
        raise ValueError("Invalid varint")

    while position < len(data):
        tag = varint()
        if tag >> 3 == 0 or tag >> 3 in result:
            raise ValueError("Invalid or duplicate scalar field")
        if tag & 7 == 0:
            value = varint()
        elif tag & 7 == 2:
            length = varint()
            if position + length > len(data):
                raise ValueError("Truncated field")
            value = data[position:position + length]
            position += length
        else:
            raise ValueError("Unexpected wire type in scoped fixture")
        result[tag >> 3] = value
    return result


class OriginalGoldenTests(unittest.TestCase):
    def test_original_provenance_names_upstream_and_fixed_commit(self):
        for name in ["OriginalLoginChat", "OriginalTrade"]:
            path = FIXTURES.parents[1] / name / "provenance.json"
            provenance = json.loads(path.read_text())
            self.assertEqual(provenance["sourceRepository"], "https://github.com/thomotron/Phinix")
            self.assertEqual(provenance.get("sourceSha", provenance.get("originalSha")), "ce82b8f2cd57718f05de86458eebd3320f35c4d8")
            self.assertEqual(provenance["protobufSha"], "dd06cc6c8f906b760ef224a085f45e7694dc426a")

    def test_original_module_captures_have_independent_expected_fields(self):
        capture = json.loads(FIXTURES.read_text())
        self.assertEqual(capture["sourceSha"], "ce82b8f2cd57718f05de86458eebd3320f35c4d8")
        self.assertEqual(capture["protobufSha"], "dd06cc6c8f906b760ef224a085f45e7694dc426a")
        self.assertEqual(len(capture["fixtures"]), 3)
        expected = {
            "Phinix/UserManagement.LoginPacket": {3: b"synthetic-probe-user", 4: 1},
            "Phinix/Chat.ChatMessagePacket": {2: b"SENDER", 3: b"synthetic-probe-message", 5: b"MESSAGE"},
            "Phinix/Chat.ChatMessageResponsePacket": {1: 1, 2: b"MESSAGE", 3: b"CONFIRMED_MESSAGE", 4: b"synthetic-probe-message"}
        }
        seen = set()
        for fixture in capture["fixtures"]:
            data = base64.b64decode(fixture["derivedAnyBase64"], validate=True)
            self.assertEqual(hashlib.sha256(data).hexdigest(), fixture["derivedSha256"])
            envelope = fields(data)
            self.assertEqual(set(envelope), {1, 2})
            type_url = envelope[1].decode()
            self.assertNotIn(type_url, seen)
            seen.add(type_url)
            self.assertEqual(type_url, fixture["typeUrl"])
            self.assertEqual(fields(envelope[2]), expected[type_url])
            self.assertEqual(fixture["classification"], "redacted-derived-from-original-module-loopback-capture")
        self.assertEqual(seen, set(expected))

    def test_truncation_is_rejected(self):
        with self.assertRaises(ValueError):
            fields(b"\x12\x05ab")

    def test_repeated_field_cannot_override_expected_scalar(self):
        with self.assertRaises(ValueError):
            fields(b"\x08\x01\x08\x00")

    def test_wrong_field_direction_is_not_accepted_as_expected_intent(self):
        # Original request's Message=3 and MessageId=5 are distinct semantic fields.
        wrong = fields(b"\x1a\x07MESSAGE\x2a\x17synthetic-probe-message")
        self.assertNotEqual(wrong, {3: b"synthetic-probe-message", 5: b"MESSAGE"})


if __name__ == "__main__":
    unittest.main()

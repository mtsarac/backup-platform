"""Cross-implementation check: the Python reference decryptor against the vectors produced by the .NET library.

Run: python3 -m unittest discover -s scripts/bpe-reference -v   (needs the `cryptography` package)
"""
import hashlib
import io
import itertools
import json
import os
import pathlib
import struct
import tempfile
import unittest

import bpe_decrypt
import bpe_shares

ROOT = pathlib.Path(__file__).parent / "vectors"
MANIFEST = json.loads((ROOT / "manifest.json").read_text())
CASES = {case["name"]: case for case in MANIFEST["cases"]}
KEYS = {name: bpe_decrypt.load_private_key(ROOT / entry["private_pem"]) for name, entry in MANIFEST["keys"].items()}
SHARES = sorted((ROOT / MANIFEST["recovery_shares"]["dir"]).glob("*.bpshare"))
CHUNK = MANIFEST["cases"][0]["chunk_size"]


def artifact(name):
    return (ROOT / CASES[name]["artifact"]).read_bytes()


def plaintext(name):
    return (ROOT / CASES[name]["plaintext"]).read_bytes()


def run(data, key="tenant-wrap", dek=None):
    out = io.BytesIO()
    bpe_decrypt.decrypt(io.BytesIO(data), out, private_key=KEYS[key] if dek is None else None, dek=dek)
    return out.getvalue()


class DecryptsVectors(unittest.TestCase):
    def test_every_case_every_recipient_is_byte_exact(self):
        for name, case in CASES.items():
            for recipient in case["recipients"]:
                with self.subTest(case=name, recipient=recipient):
                    result = run(artifact(name), recipient)
                    self.assertEqual(result, plaintext(name))
                    self.assertEqual(hashlib.sha256(result).hexdigest(), case["plaintext_sha256"])

    def test_header_core_matches_manifest(self):
        for name, case in CASES.items():
            with self.subTest(case=name):
                header = bpe_decrypt.read_header(io.BytesIO(artifact(name)))
                self.assertEqual(header["artifact_id"].hex(), case["artifact_id"])
                self.assertEqual(header["nonce_prefix"].hex(), case["nonce_prefix"])
                self.assertEqual(header["chunk_size"], case["chunk_size"])
                self.assertEqual(header["source_size"], case["source_size"])
                self.assertEqual([r["type"] for r in header["recipients"]], case["recipients"])
                for recipient in header["recipients"]:
                    self.assertEqual(recipient["key_id"], MANIFEST["keys"][recipient["type"]]["key_id"])

    def test_decrypts_with_the_dek_alone(self):
        for name, case in CASES.items():
            with self.subTest(case=name):
                self.assertEqual(run(artifact(name), dek=bytes.fromhex(case["dek"])), plaintext(name))

    def test_chunk_layout_matches_the_documented_size(self):
        for name, case in CASES.items():
            with self.subTest(case=name):
                header_end = 12 + struct.unpack(">I", artifact(name)[8:12])[0]
                chunks = max(1, -(-case["source_size"] // CHUNK))
                self.assertEqual(len(artifact(name)) - header_end, case["source_size"] + 16 * chunks)


class RejectsDamage(unittest.TestCase):
    def test_wrong_key_id(self):
        with self.assertRaisesRegex(bpe_decrypt.BpeError, "no recipient matches"):
            run(artifact("multi-chunk"), "platform-recovery")

    def test_truncation_everywhere_in_the_chunk_region(self):
        data = artifact("multi-chunk")
        header_end = 12 + struct.unpack(">I", data[8:12])[0]
        for cut in range(header_end, len(data)):
            with self.subTest(cut=cut), self.assertRaises(bpe_decrypt.BpeError):
                run(data[:cut])

    def test_dropping_a_whole_final_chunk_of_a_chunk_aligned_artifact(self):
        with self.assertRaises(bpe_decrypt.BpeError):
            run(artifact("exact-boundary")[:-(CHUNK + 16)])

    def test_appended_data(self):
        with self.assertRaises(bpe_decrypt.BpeError):
            run(artifact("exact-boundary") + b"x")

    def test_every_single_bit_flip_is_rejected(self):
        # Supplying the DEK keeps this fast; the recipients_mac still has to catch flips in the header.
        dek = bytes.fromhex(CASES["multi-chunk"]["dek"])
        data = bytearray(artifact("multi-chunk"))
        for offset in range(len(data)):
            data[offset] ^= 0x01
            with self.subTest(offset=offset), self.assertRaises(bpe_decrypt.BpeError):
                run(bytes(data), dek=dek)
            data[offset] ^= 0x01

    def test_reordered_chunks(self):
        data = artifact("multi-chunk")
        header_end = 12 + struct.unpack(">I", data[8:12])[0]
        size = CHUNK + 16
        swapped = data[:header_end] + data[header_end + size:header_end + 2 * size] + data[header_end:header_end + size] + data[header_end + 2 * size:]
        with self.assertRaisesRegex(bpe_decrypt.BpeError, "failed authentication"):
            run(swapped)

    def test_empty_and_garbage_input(self):
        for data in (b"", b"BPENC\0", b"x" * 64):
            with self.subTest(length=len(data)), self.assertRaises(bpe_decrypt.BpeError):
                run(data)


class RecoveryShares(unittest.TestCase):
    def key_from(self, files):
        return bpe_decrypt.key_from_shares(files)

    def test_any_three_of_five_decrypt_the_recovery_recipient(self):
        for combo in itertools.combinations(SHARES, 3):
            with self.subTest(shares=[path.name for path in combo]):
                out = io.BytesIO()
                bpe_decrypt.decrypt(io.BytesIO(artifact("two-recipients")), out, private_key=self.key_from(combo))
                self.assertEqual(out.getvalue(), plaintext("two-recipients"))

    def test_fewer_than_threshold_fails(self):
        for count in (1, 2):
            with self.subTest(count=count), self.assertRaisesRegex(bpe_shares.ShareError, "required"):
                self.key_from(SHARES[:count])

    def test_duplicate_share_is_rejected(self):
        with self.assertRaisesRegex(bpe_shares.ShareError, "more than once"):
            self.key_from([SHARES[0], SHARES[0], SHARES[1]])

    def test_tampered_share_is_rejected(self):
        raw = bpe_shares.parse_text(SHARES[0].read_text())
        for offset in (4, 5, 7, 20, 30, 60, len(raw) - 1):
            tampered = bytearray(raw)
            tampered[offset] ^= 0x01
            with self.subTest(offset=offset), self.assertRaises(bpe_shares.ShareError):
                bpe_shares.combine([bytes(tampered)] + [bpe_shares.parse_text(p.read_text()) for p in SHARES[1:3]])



class AtomicOutput(unittest.TestCase):
    def test_success_is_owner_only_and_does_not_touch_a_stale_partial(self):
        with tempfile.TemporaryDirectory() as directory:
            destination = pathlib.Path(directory) / "plain.bin"
            stale = pathlib.Path(str(destination) + ".partial")
            stale.write_text("leave untouched")
            bpe_decrypt.decrypt_file(str(ROOT / "two-recipients.bpe"), str(destination), KEYS["platform-recovery"])
            self.assertEqual(destination.read_bytes(), plaintext("two-recipients"))
            self.assertEqual(stale.read_text(), "leave untouched")
            if os.name != "nt":
                self.assertEqual(destination.stat().st_mode & 0o777, 0o600)

    def test_final_tag_failure_preserves_destination_and_removes_partial(self):
        with tempfile.TemporaryDirectory() as directory:
            destination = pathlib.Path(directory) / "plain.bin"
            destination.write_bytes(b"existing output")
            damaged = pathlib.Path(directory) / "damaged.bpe"
            data = bytearray(artifact("multi-chunk"))
            data[-1] ^= 1
            damaged.write_bytes(data)
            with self.assertRaises(bpe_decrypt.BpeError):
                bpe_decrypt.decrypt_file(str(damaged), str(destination), KEYS["tenant-wrap"])
            self.assertEqual(destination.read_bytes(), b"existing output")
            self.assertEqual(list(pathlib.Path(directory).glob("*.partial")), [])

if __name__ == "__main__":
    unittest.main()

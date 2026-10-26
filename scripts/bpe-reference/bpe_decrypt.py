#!/usr/bin/env python3
"""Stand-alone reference decryptor for bpe v2 artifacts (docs/Artifact_Encryption_Format.md).

Needs Python 3.9+ and the `cryptography` package only; no backend, database, network or .NET. It authenticates the recipient list
(recipients_mac), every chunk (AES-256-GCM with the core hash, counter and final flag as AAD) and the declared plaintext size, and writes the
plaintext to a temporary file that is renamed into place only after the whole artifact verified.

  python3 bpe_decrypt.py --in artifact.bpe --out plaintext --key private-key.pem
  python3 bpe_decrypt.py --in artifact.bpe --out plaintext --share s1 --share s2 --share s3
  python3 bpe_decrypt.py --in artifact.bpe --out plaintext --dek-hex <64 hex>   # DEK unwrapped elsewhere, e.g. with openssl
"""
import argparse
import base64
import hashlib
import hmac
import json
import os
import struct
import sys
import tempfile

import bpe_shares
from cryptography.exceptions import InvalidTag
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import padding
from cryptography.hazmat.primitives.ciphers.aead import AESGCM
from cryptography.hazmat.primitives.kdf.hkdf import HKDF

MAGIC = b"BPENC\0"
VERSION = 2
CORE_LENGTH = 33
TAG_LENGTH = 16
MAC_LENGTH = 32
MAX_CHUNK_SIZE = 64 << 20
MAX_HEADER_LENGTH = 1 << 20


class BpeError(Exception):
    pass


def _read_full(stream, size):
    parts, remaining = [], size
    while remaining:
        part = stream.read(remaining)
        if not part:
            break
        parts.append(part)
        remaining -= len(part)
    return b"".join(parts)


def key_id(private_key):
    spki = private_key.public_key().public_bytes(serialization.Encoding.DER, serialization.PublicFormat.SubjectPublicKeyInfo)
    return hashlib.sha256(spki).digest()[:16].hex()


def read_header(stream):
    preamble = _read_full(stream, 12)
    if len(preamble) != 12 or preamble[:6] != MAGIC:
        raise BpeError("not a bpe artifact")
    (version, header_length) = struct.unpack(">HI", preamble[6:])
    if version != VERSION:
        raise BpeError("unsupported bpe version")
    if header_length < CORE_LENGTH + 4 + MAC_LENGTH or header_length > MAX_HEADER_LENGTH:
        raise BpeError("invalid header length")
    header = _read_full(stream, header_length)
    if len(header) != header_length:
        raise BpeError("truncated header")
    core = header[:CORE_LENGTH]
    artifact_id, alg = core[:16], core[16]
    (chunk_size,) = struct.unpack(">I", core[17:21])
    nonce_prefix = core[21:25]
    (source_size,) = struct.unpack(">Q", core[25:33])
    if alg != 1:
        raise BpeError(f"unsupported algorithm id {alg}")
    if chunk_size == 0 or chunk_size > MAX_CHUNK_SIZE:
        raise BpeError("invalid chunk size")
    (recipients_length,) = struct.unpack(">I", header[CORE_LENGTH:CORE_LENGTH + 4])
    if recipients_length != header_length - CORE_LENGTH - 4 - MAC_LENGTH:
        raise BpeError("invalid recipients length")
    recipients_json = header[CORE_LENGTH + 4:-MAC_LENGTH]
    try:
        recipients = [
            {"type": r["type"], "key_id": r["key_id"], "key_version": r["key_version"], "wrapped_dek": base64.b64decode(r["wrapped_dek"], validate=True)}
            for r in json.loads(recipients_json)
        ]
    except (ValueError, KeyError, TypeError) as error:
        raise BpeError("recipient list is malformed") from error
    return {
        "core_hash": hashlib.sha256(core).digest(), "chunk_size": chunk_size, "nonce_prefix": nonce_prefix, "source_size": source_size,
        "artifact_id": artifact_id, "recipients_json": recipients_json, "mac": header[-MAC_LENGTH:], "recipients": recipients,
    }


def unwrap_dek(header, private_key):
    wanted = key_id(private_key)
    for recipient in header["recipients"]:
        if recipient["key_id"] == wanted:
            try:
                return private_key.decrypt(
                    recipient["wrapped_dek"],
                    padding.OAEP(mgf=padding.MGF1(hashes.SHA256()), algorithm=hashes.SHA256(), label=None))
            except ValueError as error:
                raise BpeError("the wrapped key is invalid") from error
    raise BpeError("no recipient matches the supplied private key (recipients: "
                   + ", ".join(f"{r['type']} {r['key_id']}" for r in header["recipients"]) + ")")


def decrypt(stream, out, private_key=None, dek=None):
    """Decrypts `stream` into `out`. The caller MUST discard `out` when this raises; see decrypt_file."""
    header = read_header(stream)
    if dek is None:
        dek = unwrap_dek(header, private_key)
    if len(dek) != 32:
        raise BpeError("the unwrapped key has an invalid length")
    mac_key = HKDF(algorithm=hashes.SHA256(), length=32, salt=None, info=b"bpe-recipients-mac").derive(dek)
    expected = hmac.new(mac_key, header["core_hash"] + header["recipients_json"], hashlib.sha256).digest()
    if not hmac.compare_digest(expected, header["mac"]):
        raise BpeError("recipient list failed authentication")
    aes = AESGCM(dek)
    sealed_size = header["chunk_size"] + TAG_LENGTH
    current = _read_full(stream, sealed_size)
    total, counter = 0, 0
    while True:
        if len(current) < TAG_LENGTH:
            raise BpeError("truncated artifact: missing final chunk")
        following = _read_full(stream, sealed_size) if len(current) == sealed_size else b""
        is_final = not following
        nonce = header["nonce_prefix"] + struct.pack(">Q", counter)
        aad = header["core_hash"] + struct.pack(">QB", counter, 1 if is_final else 0)
        try:
            plain = aes.decrypt(nonce, current, aad)
        except InvalidTag as error:
            raise BpeError(f"chunk {counter} failed authentication (corrupt, reordered, or truncated)") from error
        out.write(plain)
        total += len(plain)
        if is_final:
            break
        current, counter = following, counter + 1
    if total != header["source_size"]:
        raise BpeError("decrypted size does not match the declared source size")


def decrypt_file(in_path, out_path, private_key=None, dek=None):
    temp = None
    try:
        with open(in_path, "rb") as stream, tempfile.NamedTemporaryFile(
                mode="wb", dir=os.path.dirname(os.path.abspath(out_path)),
                prefix=os.path.basename(out_path) + ".", suffix=".partial", delete=False) as out:
            temp = out.name
            decrypt(stream, out, private_key, dek)
        os.replace(temp, out_path)
    except BaseException:
        if temp is not None and os.path.exists(temp):
            os.remove(temp)
        raise


def load_private_key(path):
    with open(path, "rb") as handle:
        return serialization.load_pem_private_key(handle.read(), password=None)


def key_from_shares(paths):
    texts = []
    for path in paths:
        with open(path, encoding="ascii") as handle:
            texts.append(handle.read())
    secret = bpe_shares.combine([bpe_shares.parse_text(text) for text in texts])
    return serialization.load_der_private_key(secret, password=None)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--in", dest="source", required=True)
    parser.add_argument("--out", required=True)
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument("--key")
    group.add_argument("--share", action="append")
    group.add_argument("--dek-hex")
    args = parser.parse_args(argv)
    try:
        if args.dek_hex:
            decrypt_file(args.source, args.out, dek=bytes.fromhex(args.dek_hex))
        else:
            key = load_private_key(args.key) if args.key else key_from_shares(args.share)
            decrypt_file(args.source, args.out, private_key=key)
    except (BpeError, bpe_shares.ShareError, OSError, ValueError) as error:
        print(f"bpe_decrypt: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())

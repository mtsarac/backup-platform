"""Reference implementation of the Recovery Key share format (see docs/Artifact_Encryption_Format.md, section "Recovery key shares").

Standard library only. Combines Shamir shares over GF(256) (AES polynomial 0x11B) and verifies every integrity field of the share encoding.
"""
import base64
import hashlib
import struct

TEXT_PREFIX = "BPSHARE1:"
_HEADER = 4 + 1 + 1 + 1 + 1 + 16 + 16 + 2
_CHECKSUM = 8


class ShareError(Exception):
    pass


def _tables():
    exp, log, x = [0] * 510, [0] * 256, 1
    for i in range(255):
        exp[i] = exp[i + 255] = x
        log[x] = i
        x ^= (x << 1) ^ (0x11B if x & 0x80 else 0)  # multiply by the generator 3
    return exp, log


_EXP, _LOG = _tables()


def _mul(a, b):
    return 0 if a == 0 or b == 0 else _EXP[_LOG[a] + _LOG[b]]


def _div(a, b):
    return 0 if a == 0 else _EXP[_LOG[a] + 255 - _LOG[b]]


def parse_text(text):
    text = text.strip()
    if not text.startswith(TEXT_PREFIX):
        raise ShareError("not a bpe share (missing BPSHARE1: prefix)")
    try:
        return base64.b64decode(text[len(TEXT_PREFIX):], validate=True)
    except ValueError as error:
        raise ShareError("the share text is not valid base64") from error


def _parse(share):
    if len(share) < _HEADER + 1 + _CHECKSUM or share[:4] != b"BPSH":
        raise ShareError("not a bpe share")
    if share[4] != 1:
        raise ShareError(f"unsupported share version {share[4]}")
    (data_len,) = struct.unpack(">H", share[_HEADER - 2:_HEADER])
    if len(share) != _HEADER + data_len + _CHECKSUM or hashlib.sha256(share[:-_CHECKSUM]).digest()[:_CHECKSUM] != share[-_CHECKSUM:]:
        raise ShareError("the share is damaged (length or checksum mismatch)")
    threshold, total, index = share[5], share[6], share[7]
    if threshold < 2 or threshold > total or index < 1 or index > total:
        raise ShareError("the share header is invalid")
    return {
        "threshold": threshold, "total": total, "index": index, "set_id": share[8:24], "check": share[24:40],
        "data": share[_HEADER:_HEADER + data_len],
    }


def combine(shares):
    """Reconstructs the secret from raw share bytes; raises ShareError for any damaged, mixed, duplicate, insufficient or forged input."""
    if not shares:
        raise ShareError("no shares were supplied")
    parsed = [_parse(share) for share in shares]
    first = parsed[0]
    if any(p["threshold"] != first["threshold"] or p["total"] != first["total"] or p["set_id"] != first["set_id"]
           or p["check"] != first["check"] or len(p["data"]) != len(first["data"]) for p in parsed):
        raise ShareError("the shares belong to different sets")
    if len({p["index"] for p in parsed}) != len(parsed):
        raise ShareError("the same share was supplied more than once")
    if len(parsed) < first["threshold"]:
        raise ShareError(f"{first['threshold']} distinct shares are required but only {len(parsed)} were supplied")
    used = parsed[:first["threshold"]]
    weights = []
    for share in used:
        weight = 1
        for other in used:
            if other["index"] != share["index"]:
                weight = _mul(weight, _div(other["index"], share["index"] ^ other["index"]))
        weights.append(weight)
    secret = bytearray(len(first["data"]))
    for b in range(len(secret)):
        value = 0
        for weight, share in zip(weights, used):
            value ^= _mul(weight, share["data"][b])
        secret[b] = value
    if hashlib.sha256(secret).digest()[:16] != first["check"]:
        raise ShareError("the shares do not reconstruct the original secret: at least one share is corrupted or forged")
    return bytes(secret)

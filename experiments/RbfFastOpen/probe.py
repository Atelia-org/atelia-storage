"""In-memory RBF framing proof; no production RBF2 or filesystem recovery."""
import json
import random
import struct

F1 = b"RBF1"
F2 = b"RBF2"
MARK = int.from_bytes(F2, "little")
MAX_LENGTH = (1 << 28) - 4
MAX_OFFSET = (1 << 40) - 4
TABLE = []
for value in range(256):
    for _ in range(8):
        value = (value >> 1) ^ (0x82F63B78 if value & 1 else 0)
    TABLE.append(value)


def crc_forward(data):
    value = 0xFFFFFFFF
    for byte in data:
        value = TABLE[(value ^ byte) & 255] ^ (value >> 8)
    return value ^ 0xFFFFFFFF


def crc_backward(data):
    """Match RollingCrc.CrcBackward; traversal and storage endian are independent."""
    return crc_forward(data[::-1])


crc = crc_forward


def u32(value):
    return struct.pack("<I", value)


def word(data, offset=0):
    return struct.unpack_from("<I", data, offset)[0]


def trailer(length, tag, descriptor=0):
    fields = struct.pack("<III", descriptor, tag, length)
    return struct.pack(">I", crc_backward(fields)) + fields


def xor_words(data, key):
    assert len(data) % 4 == 0
    return b"".join(u32(word(data, i) ^ key) for i in range(0, len(data), 4))


def encode(payload, meta=b"", tag=11, *, tombstone=False):
    pad = (-len(payload) - len(meta)) % 4
    coverage = payload + meta + bytes(pad)
    length = len(coverage) + 28
    assert 28 <= length <= MAX_LENGTH and len(meta) <= 65535
    descriptor = pad << 29 | len(meta) | (0x80000000 if tombstone else 0)
    plain = coverage + u32(crc(coverage)) + trailer(length, tag, descriptor)
    m = len(plain) // 4
    forbidden = {word(plain, i) ^ MARK for i in range(0, len(plain), 4)}
    key = next(k for k in range(m + 1) if k not in forbidden)
    frame = u32(length) + xor_words(plain, key) + u32(key)
    assert key <= m and key != MARK
    assert all(word(frame, i) != MARK for i in range(0, len(frame), 4))
    return frame + F2


def read_structural_tail(read, eof):
    """Fixed tail/head/padding reads; excludes Header and all PayloadCRC validation.

    A successful local parse does not by itself establish file-profile or membership.
    The caller must have checked Header and obtained the real EOF/boundary evidence.
    """
    assert eof >= 36 and eof % 4 == 0
    tail = read(eof - 24, 24)
    assert len(tail) == 24 and tail[-4:] == F2
    assert all(word(tail, i) != MARK for i in range(0, 16, 4))
    key = word(tail, 16)
    fields = xor_words(tail[:16], key)
    descriptor, tag, length = struct.unpack_from("<III", fields, 4)
    assert fields[:4] == struct.pack(">I", crc_backward(fields[4:]))
    assert 28 <= length <= MAX_LENGTH and length % 4 == 0
    assert key <= (length - 8) // 4
    start = eof - 4 - length
    assert 4 <= start <= MAX_OFFSET and start % 4 == 0
    left = read(start - 4, 8)
    assert len(left) == 8 and left[:4] == F2 and word(left, 4) == length
    assert descriptor & 0x1FFF0000 == 0
    pad, meta = descriptor >> 29 & 3, descriptor & 65535
    assert length - 28 >= pad + meta
    if pad:
        encoded_padding = read(start + length - 24 - pad, pad)
        assert len(encoded_padding) == pad
        mask = u32(key)
        assert bytes(value ^ mask[(4 - pad + i) % 4] for i, value in enumerate(encoded_padding)) == bytes(pad)
    return start, descriptor, tag, key, length


def open_structural(read, eof):
    """Header4 + tail24 + left-Fence/HeadLen8 + padding<=3: at most 39 bytes."""
    assert eof >= 4 and read(0, 4) == F2
    return None if eof == 4 else read_structural_tail(read, eof)


def validate_tail_structure(image, on_read=None):
    def read(offset, count):
        assert 0 <= offset <= len(image) and count <= len(image) - offset
        if on_read is not None:
            on_read(offset, count)
        return image[offset:offset + count]
    return open_structural(read, len(image))


def validate_tail(image):
    """Complete ReadFrame reference: both CRCs and encoded-body constraints.

    Intentionally local, without Header dispatch, for the legacy embedded-tail counterexample.
    Ordinary Open uses validate_tail_structure instead and does not read coverage/PayloadCRC.
    """
    start, descriptor, tag, key, length = read_structural_tail(lambda o, n: image[o:o + n], len(image))
    body = xor_words(image[start + 4:-8], key)
    assert word(body, len(body) - 20) == crc(body[:-20])
    assert all(word(image, i) != MARK for i in range(start, len(image) - 4, 4))
    return start, descriptor, tag


def check_prefixes(image, boundaries):
    for end in range(4, len(image) + 1):
        if end % 4 == 0 and image[end - 4:end] == F2:
            assert end in boundaries, (end, boundaries)
            if end != 4:
                validate_tail(image[:end])
    return len(image) - 3


def parallel_chains(n):
    image = bytearray(4 + 68 * n + 32)
    image[:4] = F1
    image[32:36] = F1
    for i in range(n + 1):
        head = 4 + 68 * i
        image[head:head + 4] = u32(64)
        if i < n:
            image[head + 32:head + 36] = u32(64)
            image[head + 48:head + 64] = trailer(64, 11)
            image[head + 64:head + 68] = F1
        if i > 0:
            image[head + 12:head + 28] = trailer(64, 22)
            image[head + 28:head + 32] = F1
    for i in range(n):
        head = 4 + 68 * i
        image[head + 44:head + 48] = u32(crc(image[head + 4:head + 44]))
        false = head + 32
        image[false + 44:false + 48] = u32(crc(image[false + 4:false + 44]))
    for delta in (0, 32):
        for i in range(n):
            start = 4 + 68 * i + delta
            assert word(image, start) == word(image, start + 60) == 64
            assert image[start - 4:start] == image[start + 64:start + 68] == F1
            assert word(image, start + 44) == crc(image[start + 4:start + 44])
            assert image[start + 48:start + 52] == struct.pack(">I", crc_backward(image[start + 52:start + 64]))
    assert len(image) == 4 + 68 * n + 32
    return bytes(image)


def force_crc(data, offset, target):
    """Solve a forward CRC32C patch for adversarial writer/qualification fixtures."""
    data = bytearray(data)
    data[offset:offset + 4] = bytes(4)
    base = crc(data)
    basis = {}
    for bit in range(32):
        trial = bytearray(data)
        trial[offset + bit // 8] ^= 1 << (bit % 8)
        value, mask = crc(trial) ^ base, 1 << bit
        while value:
            pivot = value.bit_length() - 1
            if pivot in basis:
                value ^= basis[pivot][0]
                mask ^= basis[pivot][1]
            else:
                basis[pivot] = value, mask
                break
    assert len(basis) == 32
    value, mask = target ^ base, 0
    while value:
        pivot = value.bit_length() - 1
        value ^= basis[pivot][0]
        mask ^= basis[pivot][1]
    data[offset:offset + 4] = u32(mask)
    assert crc(data) == target
    return bytes(data)


def run():
    assert crc(b"123456789") == 0xE3069283
    fields = bytes.fromhex("000000000b00000040000000")
    assert crc_forward(fields) == 0x19B96909
    assert crc_backward(fields) == 0x77F637B5
    assert trailer(24, 11).hex() == "6b4c6f70000000000b00000018000000"
    old = parallel_chains(64)
    rng = random.Random(20261001)
    base = F2 + encode(b"prior") + encode(b"prior two", tag=MARK)
    boundaries = {4, 4 + len(encode(b"prior")), len(base)}
    fixtures = []
    for size in (0, 1, 4, 16, 37, 96):
        fixtures.extend((encode(rng.randbytes(size), b"meta"), encode((F1 + F2) * size, tag=MARK)))
    # The CRC word itself forbids key=0; it too must participate in key selection.
    forced = force_crc(bytes(8), 0, MARK)
    fixtures.append(encode(forced))
    assert word(fixtures[-1], len(fixtures[-1]) - 8) != 0
    fixtures.append(encode(b"already closed tombstone", tombstone=True))
    prefixes = sum(check_prefixes(base + frame, boundaries | {len(base) + len(frame)}) for frame in fixtures)
    return {"model_only": True, "crc_standard_vector": "passed",
            "parallel_old_frames": 64, "parallel_false_frames": 64,
            "parallel_image_bytes": len(old), "new_format_fixtures": len(fixtures),
            "normal_writer_prefixes": prefixes, "crc_word_participates_in_key_selection": True,
            "closed_tombstone_supported": True, "trailer_crc_direction": "backward",
            "golden_trailer_vectors": 2, "recovery_tombstone_generation": False,
            "layout": "raw HeadLen + encoded body + one raw tail Key; fixed overhead 28B",
            "head_key_present": False, "production_code_or_io_exercised": False}


if __name__ == "__main__":
    print(json.dumps(run(), indent=2))

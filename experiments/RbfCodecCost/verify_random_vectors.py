"""Independent bitwise CRC32C / explicit-key wire oracle; never imports the C# or old model."""
import json
from pathlib import Path
import struct
import sys

FENCE = 0x32464252


def crc(data):
    value = 0xFFFFFFFF
    for byte in data:
        value ^= byte
        for _ in range(8):
            value = (value >> 1) ^ (0x82F63B78 if value & 1 else 0)
    return value ^ 0xFFFFFFFF


def main():
    output = Path(sys.argv[1]).resolve()
    vectors = json.loads((output / "random-vectors.json").read_text())
    high_keys = 0
    for item in vectors:
        payload, meta = bytes.fromhex(item["PayloadHex"]), bytes.fromhex(item["MetaHex"])
        key = item["Key"]
        assert 0 <= key <= 0xFFFFFFFF and key != FENCE
        pad = (-(len(payload) + len(meta))) & 3
        coverage = payload + meta + bytes(pad)
        length = len(coverage) + 28
        fields = struct.pack("<III", pad << 29 | len(meta), item["Tag"], length)
        body = coverage + struct.pack("<I", crc(coverage)) + struct.pack(">I", crc(fields[::-1])) + fields
        words = [struct.unpack_from("<I", body, i)[0] ^ key for i in range(0, len(body), 4)]
        assert FENCE not in words
        wire = struct.pack("<I", length) + b"".join(struct.pack("<I", word) for word in words) + struct.pack("<II", key, FENCE)
        assert wire == bytes.fromhex(item["WireHex"])
        assert struct.unpack_from("<I", wire, length - 8)[0] ^ length == key
        high_keys += key > len(body) // 4
    result = {"random_xor_vectors": len(vectors), "keys_above_old_m_bound": high_keys,
              "crc_oracle": "Independent Python bitwise CRC32C, forward coverage and reverse-byte trailer"}
    (output / "python-random-verification.json").write_text(json.dumps(result, indent=2) + "\n")
    print(json.dumps(result))


if __name__ == "__main__":
    main()

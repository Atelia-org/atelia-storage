"""Independent bitwise CRC/wire oracle for production Data outputs using legacy byte-length fixtures."""
import json
from pathlib import Path
import struct
import sys

from verify_random_vectors import crc

FENCE = 0x32464252


def main():
    output = Path(sys.argv[1]).resolve()
    vectors = json.loads((output / "data-vectors.json").read_text())
    tiny = 0
    for item in vectors:
        payload, meta = bytes.fromhex(item["PayloadHex"]), bytes.fromhex(item["MetaHex"])
        key = item["Key"]
        assert key != FENCE
        pad = (-(len(payload) + len(meta))) & 3
        coverage = payload + meta + bytes(pad)
        length = len(coverage) + 28
        fields = struct.pack("<III", pad << 29 | len(meta), item["Tag"], length)
        body = coverage + struct.pack("<I", crc(coverage)) + struct.pack(">I", crc(fields[::-1])) + fields
        plain_words = [struct.unpack_from("<I", body, i)[0] for i in range(0, len(body), 4)]
        encoded_words = [word ^ key for word in plain_words]
        assert FENCE not in encoded_words
        if len(body) <= 252:
            forbidden = {word ^ FENCE for word in plain_words}
            minimum = next(candidate for candidate in range(64) if candidate not in forbidden)
            assert key == minimum
            tiny += 1
        wire = struct.pack("<I", length) + b"".join(struct.pack("<I", word) for word in encoded_words)
        wire += struct.pack("<II", key, FENCE)
        assert wire == bytes.fromhex(item["WireHex"])
    result = {"data_vectors": len(vectors), "tiny_minimum_oracles": tiny,
              "oracle": "Independent Python bitwise CRC32C and LE word XOR",
              "scope": "Data foundation legacy byte-length fixtures, not units RBF qualification"}
    (output / "python-data-verification.json").write_text(json.dumps(result, indent=2) + "\n")
    print(json.dumps(result))


if __name__ == "__main__":
    main()

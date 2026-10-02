"""Independent Python wire oracle for the C# XOR and uint32-addition fixtures."""
import json
from pathlib import Path
import struct
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "RbfFastOpen"))
from probe import F2, crc, encode, trailer, word  # table-based CRC, separate language implementation


def encode_addition(payload, meta, tag):
    pad = (-(len(payload) + len(meta))) & 3
    length = len(payload) + len(meta) + pad + 28
    coverage = payload + meta + bytes(pad)
    body = coverage + struct.pack("<I", crc(coverage)) + trailer(length, tag, pad << 29 | len(meta))
    marker = word(F2)
    forbidden = {(marker - word(body, i)) & 0xFFFFFFFF for i in range(0, len(body), 4)}
    key = 0
    while key in forbidden:
        key += 1
    assert key <= len(body) // 4
    encoded = b"".join(struct.pack("<I", (word(body, i) + key) & 0xFFFFFFFF) for i in range(0, len(body), 4))
    return struct.pack("<I", length) + encoded + struct.pack("<I", key) + F2


def main():
    output = Path(sys.argv[1]).resolve()
    vectors = json.loads((output / "vectors.json").read_text())
    for item in vectors:
        payload, meta = bytes.fromhex(item["PayloadHex"]), bytes.fromhex(item["MetaHex"])
        assert encode(payload, meta, item["Tag"]) == bytes.fromhex(item["XorWireHex"])
        assert encode_addition(payload, meta, item["Tag"]) == bytes.fromhex(item["AdditionWireHex"])
    result = {"xor_vectors": len(vectors), "addition_vectors": len(vectors), "crc_oracle": "Python table-based CRC32C, plaintext coverage forward and trailer backward"}
    (output / "python-verification.json").write_text(json.dumps(result, indent=2) + "\n")
    print(json.dumps(result))


if __name__ == "__main__":
    main()

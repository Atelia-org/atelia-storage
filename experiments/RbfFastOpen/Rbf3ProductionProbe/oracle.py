"""Independent RBF3 units encoder/decoder: Python bitwise CRC32C, LE words.

No import of historical prototypes or production codec. Full CRC checking is
limited to small golden/production vectors; maximum frame checks are structural.
"""
import argparse
import hashlib
import json
from pathlib import Path
import struct

F = 0x33464252
MAX_L = (1 << 28) - 4


def crc(data):
    result = 0xFFFFFFFF
    for value in data:
        result ^= value
        for _ in range(8):
            result = (result >> 1) ^ (0x82F63B78 if result & 1 else 0)
    return result ^ 0xFFFFFFFF


def write_json(path, value):
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n", encoding="utf-8", newline="\n")


def encode(payload, meta, tag, key, legacy=False):
    pad = (-(len(payload) + len(meta))) & 3
    coverage = payload + meta + bytes(pad)
    length = len(coverage) + (24 if legacy else 28)
    units = length if legacy else length // 4
    fields = struct.pack("<III", (pad << 29) | len(meta), tag, units)
    body = coverage + struct.pack("<I", crc(coverage)) + struct.pack(">I", crc(fields[::-1])) + fields
    if legacy:
        return b"RBF1" + struct.pack("<I", length) + body + b"RBF1"
    assert key != F
    encoded = b"".join(struct.pack("<I", struct.unpack_from("<I", body, i)[0] ^ key)
                       for i in range(0, len(body), 4))
    assert all(struct.unpack_from("<I", encoded, i)[0] != F for i in range(0, len(encoded), 4))
    assert 7 <= units < 1 << 26 and 28 <= length <= MAX_L
    return b"RBF3" + struct.pack("<I", units) + encoded + struct.pack("<I", key) + b"RBF3"


def decode(wire):
    assert wire[:4] == b"RBF3" and wire[-4:] == b"RBF3"
    units = struct.unpack_from("<I", wire, 4)[0]
    length = units * 4
    assert 7 <= units < 1 << 26 and len(wire) == length + 8
    key = struct.unpack_from("<I", wire, len(wire) - 8)[0]
    assert key != F
    encoded = wire[8:-8]
    assert all(struct.unpack_from("<I", encoded, i)[0] != F for i in range(0, len(encoded), 4))
    body = b"".join(struct.pack("<I", struct.unpack_from("<I", encoded, i)[0] ^ key)
                    for i in range(0, len(encoded), 4))
    descriptor, tag, tail = struct.unpack_from("<III", body, len(body) - 12)
    assert tail == units
    assert struct.unpack_from("<I", wire, len(wire) - 12)[0] ^ units == key
    assert descriptor & 0x1FFF0000 == 0
    pad, meta_len = descriptor >> 29 & 3, descriptor & 65535
    fields = body[-12:]
    assert struct.unpack_from(">I", body, len(body) - 16)[0] == crc(fields[::-1])
    coverage = body[:-20]
    assert struct.unpack_from("<I", body, len(body) - 20)[0] == crc(coverage)
    assert not pad or coverage[-pad:] == bytes(pad)
    plain = coverage[:-pad] if pad else coverage
    return plain[:-meta_len] if meta_len else plain, plain[-meta_len:] if meta_len else b"", tag, key


def fixtures(output):
    folder = output / "fixtures"
    folder.mkdir(parents=True, exist_ok=False)
    specs = [
        ("empty-key0", b"", b"", 11, 0),
        ("phase1-key0", b"a", b"xyz", 17, 0),
        ("phase3-high", b"abc", b"q", 23, 0x89ABCDEF),
        ("dense-key301", b"".join(struct.pack("<I", F ^ k) for k in range(301)), b"", 29, 301),
        ("max-meta-high", b"abc", bytes((i * 17 + 3) & 255 for i in range(65535)), 31, 0xFFFFFFFF),
    ]
    manifest = []
    for name, payload, meta, tag, key in specs:
        wire = encode(payload, meta, tag, key)
        assert decode(wire) == (payload, meta, tag, key)
        (folder / (name + ".rbf")).write_bytes(wire)
        manifest.append(dict(Name=name, File=name + ".rbf", Profile="RBF3", Key=key,
                             Tag=tag, Length=len(wire)-8, PayloadHex=payload.hex(), MetaHex=meta.hex(),
                             Sha256=hashlib.sha256(wire).hexdigest()))
    wire = encode(b"old", b"m", 37, 0, legacy=True)
    (folder / "legacy.rbf").write_bytes(wire)
    manifest.append(dict(Name="legacy", File="legacy.rbf", Profile="RBF1", Key=0,
                         Tag=37, Length=len(wire)-8, PayloadHex=b"old".hex(), MetaHex=b"m".hex(),
                         Sha256=hashlib.sha256(wire).hexdigest()))
    write_json(folder / "manifest.json", manifest)
    write_json(output / "python-fixtures.json", dict(Passed=True, Fixtures=len(manifest),
               Oracle="Independent bitwise CRC32C and RBF3 units encoding; RBF1 byte wire separate"))


def verify(output):
    vectors = json.loads((output / "production-vectors.json").read_text(encoding="utf-8"))
    for item in vectors:
        wire = bytes.fromhex(item["WireHex"])
        payload, meta = bytes.fromhex(item["PayloadHex"]), bytes.fromhex(item["MetaHex"])
        decoded = decode(wire)
        assert decoded[:3] == (payload, meta, item["Tag"])
        assert encode(payload, meta, item["Tag"], decoded[3]) == wire
    result = dict(Passed=True, ProductionFullWireVectors=len(vectors),
                  Oracle="Independent Python bitwise CRC32C, units, marker exclusion, XOR and exact wire")
    max_file = output / "io" / "open-n1-max.rbf"
    if max_file.exists():
        with max_file.open("rb") as stream:
            head = stream.read(8)
            stream.seek(-24, 2)
            tail = stream.read(24)
        units = struct.unpack_from("<I", head, 4)[0]
        key = struct.unpack_from("<I", tail, 16)[0]
        plain_trailer = b"".join(struct.pack("<I", struct.unpack_from("<I", tail, i)[0] ^ key)
                                  for i in range(0, 16, 4))
        assert head[:4] == tail[-4:] == b"RBF3"
        assert units == (1 << 26) - 1 and max_file.stat().st_size == units * 4 + 8
        assert key != F and struct.unpack_from("<I", tail, 12)[0] ^ units == key
        assert struct.unpack_from("<I", plain_trailer, 12)[0] == units
        assert struct.unpack_from(">I", plain_trailer, 0)[0] == crc(plain_trailer[4:][::-1])
        assert struct.unpack_from("<I", plain_trailer, 4)[0] & 65535 == 65535
        result["MaximumFrame"] = dict(Bytes=units * 4, Key=key, FileBytes=max_file.stat().st_size,
             IndependentScope="Header/units/raw key/Fence/decoded 12B trailer fields and bitwise TrailerCRC; not full payload CRC")
    write_json(output / "python-verification.json", result)
    print(json.dumps(result))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("action", choices=["fixtures", "verify"])
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    {"fixtures": fixtures, "verify": verify}[args.action](args.output.resolve())

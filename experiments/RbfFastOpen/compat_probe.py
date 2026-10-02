"""RBF1 read compatibility and Header dispatch; no production RBF2 or upgrade I/O."""
import argparse
import json
import sys
from pathlib import Path

sys.dont_write_bytecode = True
from probe import F1, F2, MAX_LENGTH, crc, crc_backward, encode, trailer, u32, validate_tail, word, xor_words


def encode_old(payload, meta=b"", tag=11):
    pad = (-len(payload) - len(meta)) % 4
    coverage = payload + meta + bytes(pad)
    length = len(coverage) + 24
    assert 24 <= length <= MAX_LENGTH and len(meta) <= 65535
    return u32(length) + coverage + u32(crc(coverage)) + trailer(length, tag, pad << 29 | len(meta)) + F1


def parse_at(image, start, new):
    length = word(image, start)
    assert (28 if new else 24) <= length <= MAX_LENGTH and length % 4 == 0
    end = start + length
    assert end + 4 <= len(image)
    assert image[end:end + 4] == (F2 if new else F1)
    if new:
        validate_tail(F2 + image[start:end + 4])
        key = word(image, end - 4)
        body = xor_words(image[start + 4:end - 4], key)
        coverage, footer = body[:-20], body[-16:]
        assert word(body, len(body) - 20) == crc(coverage)
    else:
        coverage = image[start + 4:end - 20]
        footer = image[end - 16:end]
        assert word(image, end - 20) == crc(coverage)
    assert footer[:4] == crc_backward(footer[4:]).to_bytes(4, "big")
    descriptor, tag, tail_length = word(footer, 4), word(footer, 8), word(footer, 12)
    assert tail_length == length and descriptor & 0x1FFF0000 == 0
    pad, meta_len = descriptor >> 29 & 3, descriptor & 65535
    assert pad + meta_len <= len(coverage)
    if pad:
        assert coverage[-pad:] == bytes(pad)
    logical = coverage[:len(coverage) - pad] if pad else coverage
    return end + 4, (start, length, logical[:len(logical) - meta_len], logical[len(logical) - meta_len:], tag)


def strict_read(image):
    """Complete-image reference decoder; checks every CRC, unlike ordinary Open."""
    profile = image[:4]
    assert profile in (F1, F2)
    start, records = 4, []
    while start < len(image):
        start, record = parse_at(image, start, profile == F2)
        records.append(record)
    assert start == len(image)
    return records


def structural_open_old(image, on_read=None):
    """RBF1 target Open: full framing chain, no PayloadCRC or coverage read.

    This is a proposed reference, not current production behavior. No local EOF shortcut.
    """
    def read(offset, count):
        assert 0 <= offset and offset + count <= len(image)
        if on_read is not None:
            on_read(offset, count)
        return image[offset:offset + count]

    assert len(image) >= 4 and read(0, 4) == F1
    start, records = 4, []
    while start < len(image):
        length = word(read(start, 4))
        assert 24 <= length <= MAX_LENGTH and length % 4 == 0
        end = start + length
        assert end + 4 <= len(image)
        footer = read(end - 16, 16)
        assert footer[:4] == crc_backward(footer[4:]).to_bytes(4, "big")
        descriptor, tag, tail_length = word(footer, 4), word(footer, 8), word(footer, 12)
        assert tail_length == length and descriptor & 0x1FFF0000 == 0
        padding, meta = descriptor >> 29 & 3, descriptor & 65535
        coverage_length = length - 24
        assert padding + meta <= coverage_length
        if padding:
            assert read(end - 20 - padding, padding) == bytes(padding)
        assert read(end, 4) == F1
        records.append((start, length, tag, coverage_length - padding - meta, meta))
        start = end + 4
    assert start == len(image)
    return records


def run():
    assert (F1 + encode_old(b"", tag=11)).hex() == "5242463118000000000000006b4c6f70000000000b0000001800000052424631"
    legacy, expected = F1, []
    for payload, meta, tag in ((b"old-a", b"m", 1), (F2 * 9, b"old-meta", 2), (b"", b"", 3)):
        frame = encode_old(payload, meta, tag)
        expected.append((len(legacy), word(frame), payload, meta, tag))
        legacy += frame
    assert strict_read(legacy) == expected
    reads = []
    structural = structural_open_old(legacy, lambda offset, count: reads.append((offset, count)))
    assert [(x[0], x[1], x[2]) for x in structural] == [(x[0], x[1], x[4]) for x in expected]
    corrupted = bytearray(legacy)
    last_start, last_length = expected[-1][:2]
    corrupted[last_start + last_length - 20] ^= 1  # Only last PayloadCRC, not Trailer.
    assert structural_open_old(bytes(corrupted)) == structural
    try:
        strict_read(bytes(corrupted))
    except AssertionError:
        pass
    else:
        raise AssertionError("Full legacy reference accepted the corrupted last PayloadCRC")
    assert strict_read(F2 + encode(b"new"))[0][2] == b"new"
    # An embedded new frame can close at EOF inside an incomplete OLD payload.
    inner = F2 + encode(b"false tail")
    outer = encode_old(inner + b"unwritten outer suffix")
    false_image = F1 + outer[:4 + len(inner)]
    assert validate_tail(false_image)[0] == 12  # Tail parsing alone ignores Header.
    try:
        structural_open_old(false_image)
    except AssertionError:
        pass
    else:
        raise AssertionError("Legacy Header must not select the new EOF fast path")
    return {"model_only": True, "legacy_tickets_preserved": len(expected),
            "nested_new_eof_in_legacy_rejected_by_header_dispatch": True,
            "legacy_structural_open_without_payload_crc": True,
            "legacy_last_payload_crc_corruption_deferred_to_full_reference": True,
            "legacy_structural_open_reads": {"calls": len(reads), "requestedBytes": sum(n for _, n in reads)},
            "legacy_structural_open_is_proposed_not_current_production": True,
            "mixed_profile_exercised": False, "production_or_upgrade_io_exercised": False}

def export_fixtures(directory):
    """Golden RBF1 images for an independent production decoder/encoder cross-check."""
    directory = Path(directory)
    directory.mkdir(parents=True, exist_ok=True)
    fixtures = []
    inputs = {
        "header-only": [],
        "empty-frame": [(b"", b"", 11)],
        "binary-three": [(b"old-a", b"m", 1), (F2 * 9, b"old-meta", 2), (b"", b"", 3)],
        "max-meta": [(b"\x00\xff\x01\x80\x02", bytes(range(256)) * 255 + bytes(range(255)), 0xFFFFFFFF)],
    }
    for name, records in inputs.items():
        image, frames = F1, []
        for payload, meta, tag in records:
            frame = encode_old(payload, meta, tag)
            frames.append({"offset": len(image), "length": word(frame), "tag": tag,
                           "payloadHex": payload.hex(), "metaHex": meta.hex(), "isTombstone": False})
            image += frame
        assert strict_read(image) == [(f["offset"], f["length"], bytes.fromhex(f["payloadHex"]),
                                       bytes.fromhex(f["metaHex"]), f["tag"]) for f in frames]
        path = directory / (name + ".rbf")
        if path.exists() and path.read_bytes() != image:
            raise ValueError(f"Refusing to replace different fixture bytes: {path}")
        path.write_bytes(image)
        fixtures.append({"name": name, "file": path.name, "wireHex": image.hex(), "frames": frames})
    manifest = {"schemaVersion": 1, "profile": "RBF1", "fixtures": fixtures}
    manifest_path = directory / "manifest.json"
    text = json.dumps(manifest, indent=2) + "\n"
    if manifest_path.exists() and manifest_path.read_text(encoding="utf-8") != text:
        raise ValueError(f"Refusing to replace a different fixture manifest: {manifest_path}")
    manifest_path.write_text(text, encoding="utf-8", newline="\n")
    return {"directory": str(directory.resolve()), "fixtures": len(fixtures), "manifest": str(manifest_path.resolve())}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--export-fixtures", metavar="DIRECTORY", help="Export RBF1 images for ProductionProbe.")
    args = parser.parse_args()
    result = run()
    if args.export_fixtures:
        result["exported_fixtures"] = export_fixtures(args.export_fixtures)
    print(json.dumps(result, indent=2))

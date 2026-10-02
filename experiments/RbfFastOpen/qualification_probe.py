"""Tail-only Key: structural Open, truncate incomplete body, complete Key/Fence.

Uses only present bytes, with no writer oracle or unknown Trailer completion solver.
This is an in-memory model; it does not call production RBF2 or SetLength/Flush.
"""
import json
import struct
import sys
from dataclasses import dataclass

sys.dont_write_bytecode = True
from probe import (F2, MARK, MAX_LENGTH, MAX_OFFSET, encode, open_structural, trailer, u32,
                   validate_tail, validate_tail_structure, word, xor_words)


@dataclass(frozen=True)
class Qualification:
    status: str
    action: str
    detail: str


def invalid(detail):
    return Qualification("Invalid", "Reject", detail)


def incomplete(detail):
    return Qualification("IncompleteEncodedBody", "Truncated", detail)


def require_tail(image):
    try:
        return validate_tail_structure(image)
    except (AssertionError, struct.error) as error:
        raise ValueError("Closed tail structure is invalid; do not search an earlier frame.") from error


def qualify_frame(prefix):
    """Caller must establish the real boundary; local HeadLen/TrailerCRC cannot do so."""
    count = len(prefix)
    if count == 0:
        return Qualification("Complete", "None", "No append bytes.")
    if count < 4:
        head = int.from_bytes(prefix, "little")
        step = 1 << (8 * count)
        minimum = head if head >= 28 else head + (28 - head + step - 1) // step * step
        if head & 3 or minimum > MAX_LENGTH:
            return invalid("Partial HeadLen has no legal completion.")
        return incomplete("Legal partial HeadLen; discard it without guessing length.")
    length = word(prefix)
    if not 28 <= length <= MAX_LENGTH or length % 4 or count > length + 4:
        return invalid("Invalid HeadLen or more than one tail frame.")
    if count < length - 4:
        return incomplete("Encoded body incomplete; unknown Key/Trailer fields are discarded, not solved.")
    key = word(prefix, length - 8) ^ length
    if key > (length - 8) // 4:
        return invalid("Derived Key exceeds the writer's [0,m] bound.")
    if any(word(prefix, offset) == MARK for offset in range(length - 20, length - 4, 4)):
        return invalid("Visible encoded Trailer word equals Fence.")
    plain = xor_words(prefix[length - 20:length - 4], key)
    descriptor, tag, tail_length = word(plain, 4), word(plain, 8), word(plain, 12)
    if plain != trailer(length, tag, descriptor) or tail_length != length:
        return invalid("Known TrailerCRC/fields contradict the derived Key and HeadLen.")
    meta = descriptor & 65535
    padding = descriptor >> 29 & 3
    if descriptor & 0x1FFF0000 or meta + padding > length - 28:
        return invalid("Visible descriptor bits or lengths contradict the frame.")
    if padding:
        coverage_end = length - 24
        mask = u32(key)
        decoded = bytes(value ^ mask[(4 - padding + i) % 4]
                        for i, value in enumerate(prefix[coverage_end - padding:coverage_end]))
        if decoded != bytes(padding):
            return invalid("Known padding is nonzero.")
    tail_key_count = min(4, max(0, count - (length - 4)))
    if prefix[length - 4:length - 4 + tail_key_count] != u32(key)[:tail_key_count]:
        return invalid("Tail Key prefix contradicts the uniquely derived Key.")
    fence_count = max(0, count - length)
    if prefix[length:] != F2[:fence_count]:
        return invalid("Contradictory trailing Fence prefix.")
    return Qualification("Complete" if fence_count == 4 else "IncompleteTail",
                         "None" if fence_count == 4 else "CompletedTail", "Structure checked; PayloadCRC is deferred.")


def recover_frame(prefix):
    qualified = qualify_frame(prefix)
    if qualified.status == "Invalid":
        raise ValueError(qualified.detail)
    if qualified.action == "None":
        return prefix, "None"
    if qualified.action == "Truncated":
        return b"", "Truncated"
    length = word(prefix)
    key = word(prefix, length - 8) ^ length
    key_count = min(4, len(prefix) - (length - 4))
    fence_count = max(0, len(prefix) - length)
    return prefix + u32(key)[key_count:] + F2[fence_count:], "CompletedTail"


def qualify_header(image):
    if len(image) < 4:
        return Qualification("IncompleteHeader" if F2.startswith(image) else "Invalid", "Reject", "Header creation is not tail recovery.")
    if image[:4] != F2:
        return invalid("Unknown or wrong profile Header.")
    return Qualification("Complete", "None", "Header checked.")


def find_last_fence(image, max_length=MAX_LENGTH):
    """Find the FIRST reverse marker within M+7 bytes; caller validates its predecessor."""
    minimum_start = max(0, len(image) - (max_length + 7))
    for offset in range(len(image) // 4 * 4 - 4, minimum_start - 1, -4):
        if image[offset:offset + 4] == F2:
            return offset + 4
    raise ValueError("No tail boundary within MaxFrameLength+7 bytes.")


def recover_image(image, *, read_only=False):
    """Return (bytes, action, affected start); no mutation or fallback to earlier frames."""
    header = qualify_header(image)
    if header.status != "Complete":
        raise ValueError(header.detail)
    if len(image) == 4:
        return image, "None", None
    if len(image) % 4 == 0 and image[-4:] == F2:
        require_tail(image)
        return image, "None", None
    boundary = find_last_fence(image)
    if boundary > MAX_OFFSET:
        raise ValueError("Residual frame start exceeds SizedPtr.MaxOffset.")
    if boundary != 4:
        require_tail(image[:boundary])
    suffix, action = recover_frame(image[boundary:])
    if read_only and action != "None":
        raise ValueError("Read-only open requires repair; bytes remain unchanged.")
    recovered = image[:boundary] + suffix
    if len(recovered) != 4:
        require_tail(recovered)
    return recovered, action, boundary if action != "None" else None


def require_content_rejection(image):
    try:
        validate_tail(image)
    except (AssertionError, struct.error):
        return
    raise AssertionError("Complete ReadFrame reference accepted corrupted content/PayloadCRC.")


def structural_cost_evidence():
    rows = []
    for n in (1, 100, 10000):
        for last in (encode(b""), encode(b"a", b"meta"), encode(bytes(4096), b"m")):
            image = F2 + encode(b"") * (n - 1) + last
            reads = []
            validate_tail_structure(image, lambda offset, count: reads.append((offset, count)))
            total = sum(count for _, count in reads)
            assert total <= 39 and len(reads) <= 4
            rows.append({"historyFrames": n, "lastFrameBytes": word(last),
                         "readCalls": len(reads), "requestedBytes": total})

    # Structural-only: no huge coverage allocation or claim about its PayloadCRC.
    length, start, key = MAX_LENGTH, 4, 0
    descriptor = 3 << 29 | 1
    eof = start + length + 4
    segments = [(0, F2), (start, u32(length)),
                (start + length - 27, bytes(3)),
                (start + length - 20, xor_words(trailer(length, 11, descriptor), key)),
                (start + length - 4, u32(key)), (start + length, F2)]
    reads = []

    def read(offset, count):
        assert 0 <= offset and offset + count <= eof
        reads.append((offset, count))
        value = bytearray(count)
        for segment_offset, data in segments:
            lo, hi = max(offset, segment_offset), min(offset + count, segment_offset + len(data))
            if lo < hi:
                value[lo - offset:hi - offset] = data[lo - segment_offset:hi - segment_offset]
        return bytes(value)

    parsed = open_structural(read, eof)
    assert parsed[4] == MAX_LENGTH and sum(n for _, n in reads) == 39 and len(reads) == 4
    return {"ordinaryImages": rows, "virtualNearMaximum": {
        "frameBytes": length, "requestedBytes": 39, "readCalls": 4,
        "structuralOnlyFixture": True, "payloadAllocatedOrCrcValidated": False}}


def keyed_padding_phase_evidence():
    # K=1 leaves the pad's key bytes 1..3 zero; K=301 exercises actual encoded padding.
    frame = encode(b"".join(u32(MARK ^ i) for i in range(301)) + b"x")
    length, key = word(frame), word(frame, len(frame) - 8)
    assert key == 301 and frame[length - 27:length - 24] == bytes.fromhex("010000")
    validate_tail(F2 + frame)
    for cut in range(len(frame) + 1):
        image = F2 + frame[:cut]
        recovered, _, _ = recover_image(image)
        expected = F2 if 0 < cut < length - 4 else image if cut in (0, len(frame)) else F2 + frame
        assert recovered == expected and recover_image(recovered) == (recovered, "None", None)

    # High key bytes are tested with structural-only virtual images, without a huge body.
    length, start, high_key = MAX_LENGTH, 4, 0x01020304
    eof = start + length + 4
    rows = []
    for padding, encoded_hex in ((1, "01"), (2, "0201"), (3, "030201")):
        encoded_padding = u32(high_key)[4 - padding:]
        assert encoded_padding.hex() == encoded_hex
        segments = [(0, F2), (start, u32(length)),
                    (start + length - 24 - padding, encoded_padding),
                    (start + length - 20, xor_words(trailer(length, 11, padding << 29), high_key)),
                    (start + length - 4, u32(high_key)), (start + length, F2)]
        reads = []

        def read(offset, count):
            assert 0 <= offset and offset + count <= eof
            reads.append((offset, count))
            value = bytearray(count)
            for segment_offset, data in segments:
                lo, hi = max(offset, segment_offset), min(offset + count, segment_offset + len(data))
                if lo < hi:
                    value[lo - offset:hi - offset] = data[lo - segment_offset:hi - segment_offset]
            return bytes(value)

        parsed = open_structural(read, eof)
        assert parsed[3] == high_key and sum(n for _, n in reads) == 36 + padding
        rows.append({"padding": padding, "encodedPaddingHex": encoded_hex,
                     "requestedBytes": 36 + padding})
    return {"actualWriterKey": key, "actualWriterEncodedPaddingHex": "010000",
            "actualWriterPrefixCuts": len(frame) + 1, "virtualKey": high_key,
            "virtualCases": rows, "virtualPayloadAllocatedOrCrcValidated": False}


def content_orthogonality_evidence():
    original = encode(b"abcdefgh", tag=MARK)
    length = word(original)
    cases = 0
    actions = {"None": 0, "CompletedTail": 0, "Truncated": 0}
    for kind, at in (("payload", 4), ("payload-crc", length - 24)):
        damaged = bytearray(original)
        damaged[at] ^= 1
        damaged = bytes(damaged)
        assert all(word(damaged, i) != MARK for i in range(0, len(damaged) - 4, 4))
        require_content_rejection(F2 + damaged)
        for cut in range(length - 4, length + 5):
            recovered, action, _ = recover_image(F2 + damaged[:cut])
            assert recovered == F2 + damaged
            require_content_rejection(recovered)
            actions[action] += 1
            cases += 1
        discarded, action, _ = recover_image(F2 + damaged[:length - 5])
        assert discarded == F2 and action == "Truncated"
        actions[action] += 1
        cases += 1
        predecessor = F2 + damaged
        kept, action, _ = recover_image(predecessor + encode(b"uncommitted")[:9])
        assert kept == predecessor and action == "Truncated"
        require_content_rejection(kept)
        actions[action] += 1
        cases += 1
    return {"acceptedStructuralCases": cases, "actions": actions,
            "locations": ["closed", "missing-Key-0..3", "missing-Fence-0..3", "predecessor", "discarded-tail"],
            "completeReadFrameRejectedEveryCorruptedReference": True}


def boundary_counterexample():
    inner = encode(b"inner candidate", tag=2)
    inner_without_key = inner[:-8]
    outer = encode(b"p" * 16 + inner_without_key + b"unwritten outer suffix")
    assert word(outer, len(outer) - 8) == 0
    cut = 4 + 16 + len(inner_without_key)
    image = F2 + outer[:cut]
    fake_start = 24
    assert image[fake_start - 4:fake_start] != F2
    assert qualify_frame(image[fake_start:]).action == "CompletedTail"
    assert find_last_fence(image) == 4
    assert recover_image(image) == (F2, "Truncated", 4)
    return {"localTrailerCrcAndDerivedKeyPassAtFakeStart": fake_start,
            "actualBoundary": 4, "properAction": "Truncated",
            "lesson": "Local footer CRC does not establish membership; obtain the nearest real marker first."}


def rejection_matrix(frame):
    length = word(frame)
    trailer_start = length - 20
    cases = {"unaligned-head": u32(29), "too-small-head": u32(24),
             "too-large-head": u32(MAX_LENGTH + 4), "impossible-partial-head": b"\1"}

    def corrupted(end, at, mask):
        image = bytearray(frame[:end])
        image[at] ^= mask
        return bytes(image)

    cases["trailer-crc"] = corrupted(length - 4, trailer_start, 1)
    cases["reserved-descriptor"] = corrupted(length - 4, trailer_start + 7, 0x10)
    cases["encoded-tail-length"] = corrupted(length - 4, length - 8, 0x80)
    # These guards need CRC-valid malformed footers, not only mutations that fail CRC first.
    key = word(frame, length - 4)
    plain = xor_words(frame[trailer_start:length - 4], key)
    descriptor, tag = word(plain, 4), word(plain, 8)
    for name, malformed in (("crc-valid-reserved", descriptor | 0x10000),
                            ("crc-valid-meta-overflow", length - 28 + 1)):
        encoded = xor_words(trailer(length, tag, malformed), key)
        assert all(word(encoded, i) != MARK for i in range(0, 16, 4))
        cases[name] = frame[:trailer_start] + encoded
        assert qualify_frame(cases[name]).detail == "Visible descriptor bits or lengths contradict the frame."
    empty = encode(b"")
    empty_key = word(empty, len(empty) - 8)
    cases["crc-valid-empty-padding"] = empty[:8] + xor_words(trailer(28, 11, 1 << 29), empty_key)
    assert qualify_frame(cases["crc-valid-empty-padding"]).detail == "Visible descriptor bits or lengths contradict the frame."
    key = word(frame, length - 4)
    plain = xor_words(frame[length - 20:length - 4], key)
    descriptor = word(plain, 4)
    internal_marker = bytearray(frame[:length - 4])
    internal_marker[length - 20:length - 4] = xor_words(trailer(length, MARK ^ key, descriptor), key)
    cases["visible-trailer-marker"] = bytes(internal_marker)
    for visible in (1, 2, 3, 4):
        cases[f"tail-key-{visible}"] = corrupted(length - 4 + visible, length - 4, 1)
        cases[f"fence-{visible}"] = corrupted(length + visible, length, 1)
    cases["nonzero-padding"] = corrupted(length - 4, length - 25, 1)
    for name, prefix in cases.items():
        assert qualify_frame(prefix).status == "Invalid", (name, qualify_frame(prefix))
        try:
            recover_image(F2 + prefix)
        except ValueError:
            pass
        else:
            raise AssertionError(f"Invalid structure repaired: {name}")
    for count in range(4):
        try:
            recover_image(F2[:count])
        except ValueError:
            pass
        else:
            raise AssertionError("Creation residue repaired as a tail.")
    good = F2 + encode(b"valid prior")
    false_marker = good + u32(128) + F2 + b"z"
    bad_predecessor = bytearray(good)
    bad_predecessor[-24] ^= 1  # TrailerCRC, not payload: structure must reject it.
    for image in (false_marker, bytes(bad_predecessor) + frame[:9]):
        try:
            recover_image(image)
        except ValueError:
            pass
        else:
            raise AssertionError("Invalid newest marker/predecessor was bypassed.")
    return len(cases)


def run():
    nonzero = encode(b"".join(u32(MARK ^ i) for i in range(301)))
    assert word(nonzero, len(nonzero) - 8) >= 301
    keyed_padding = [encode(b"x" * n, tag=MARK) for n in (1, 2, 3)]
    assert all(word(frame, len(frame) - 8) != 0 for frame in keyed_padding)
    fixtures = [encode(b""), encode(b"a", b"meta"), encode(F2 * 8, tag=MARK),
                encode(bytes(range(37)), b"tail"), encode(bytes(range(96))), nonzero,
                encode(b"closed tombstone", tombstone=True), *keyed_padding]
    bases = [F2, F2 + encode(b"prior"), F2 + encode(b"first") + encode(b"second", b"m")]
    counts = {"original_cuts": 0, "incomplete_bodies_truncated": 0,
              "unknown_trailer_cuts_discarded": 0, "completed_tail_preserved": 0,
              "missing_key_0_1_2_3_cases": 0, "second_recovery_states": 0,
              "read_only_repairs_rejected": 0}
    for base in bases:
        for frame in fixtures:
            length = word(frame)
            for cut in range(len(frame) + 1):
                original = base + frame[:cut]
                q = qualify_frame(frame[:cut])
                assert q.status != "Invalid", (cut, q)
                recovered, action, affected = recover_image(original)
                expected = base if 0 < cut < length - 4 else original if cut in (0, len(frame)) else base + frame
                assert recovered == expected and affected == (None if action == "None" else len(base))
                counts["original_cuts"] += 1
                if action == "Truncated":
                    counts["incomplete_bodies_truncated"] += 1
                    counts["unknown_trailer_cuts_discarded"] += 0 < cut - (length - 20) < 16
                    stages = [original, recovered]
                elif action == "CompletedTail":
                    assert recovered == base + frame
                    counts["completed_tail_preserved"] += 1
                    counts["missing_key_0_1_2_3_cases"] += length - 4 <= cut < length
                    stages = [base + frame[:end] for end in range(cut, len(frame) + 1)]
                else:
                    stages = [recovered]
                for interrupted in stages:
                    again, _, _ = recover_image(interrupted)
                    assert again == recovered and recover_image(again) == (again, "None", None)
                    counts["second_recovery_states"] += 1
                if action != "None":
                    try:
                        recover_image(original, read_only=True)
                    except ValueError:
                        counts["read_only_repairs_rejected"] += 1
                    else:
                        raise AssertionError("Read-only Open silently repaired an incomplete tail.")
    rejected = rejection_matrix(fixtures[1])  # Nonzero declared padding.
    assert recover_image(F2 + u32(MAX_LENGTH)) == (F2, "Truncated", 4)
    try:
        find_last_fence(F2 + bytes(44), max_length=32)
    except ValueError:
        pass
    else:
        raise AssertionError("Boundary search escaped its M+7 window.")
    return {"model_only": True, "layout": "tail-only Key; 28B fixed overhead; payload offset 4",
            "recovery_policy": "truncate-incomplete-encoded-body; complete-Key-and-Fence",
            "fixtures": len(fixtures), "base_images": len(bases), **counts,
            "nonzero_key_fixture": word(nonzero, len(nonzero) - 8),
            "nonzero_key_padding_1_2_3_exercised": True,
            "invalid_structural_inputs_rejected": rejected,
            "boundary_lookup_exercised": True, "first_bad_marker_and_predecessor_rejected": True,
            "structural_open_cost": structural_cost_evidence(),
            "keyed_padding_phase": keyed_padding_phase_evidence(),
            "payload_crc_orthogonality": content_orthogonality_evidence(),
            "local_missing_key_counterexample": boundary_counterexample(),
            "external_writer_oracle_used": False, "unknown_trailer_completion_solved": False,
            "filesystem_or_flush_exercised": False, "production_rbf2_exercised": False}


if __name__ == "__main__":
    print(json.dumps(run(), indent=2))

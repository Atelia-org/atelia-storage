"""Independent finite-set witness; no dependency on Atelia.Data or dotnet."""

import argparse
import json
import random
from collections import Counter
from pathlib import Path


def permutation(kind, key, size):
    if kind == "add":
        return [(value + key) % size for value in range(size)]
    result = [value ^ key for value in range(size)]
    if kind == "zero_preserving_xor":
        # Construct the entire permutation, then exchange the two output slots.
        # This is independent of the production scalar branch implementation.
        result[0], result[key] = result[key], result[0]
    return result


def finite_witness(kind):
    size, limit = 256, 15
    permutations = [permutation(kind, key, size) for key in range(size)]
    universe = set(range(size))
    bijective = all(set(p) == universe for p in permutations)
    inverse_roundtrips = True
    for p in permutations:
        inverse = [None] * size
        for value, encoded in enumerate(p):
            inverse[encoded] = value
        inverse_roundtrips &= all(inverse[p[x]] == x for x in range(size))
    fixed_zero = all(p[0] == 0 for p in permutations)
    self_inverse = all(p[p[x]] == x for p in permutations for x in range(size))
    accepted = [set(p[1 : limit + 1]) for p in permutations]
    histogram = Counter(
        len(accepted[a] & accepted[b])
        for a in range(size)
        for b in range(a + 1, size)
    )
    pairs = sum(histogram.values())
    total_overlap = sum(overlap * count for overlap, count in histogram.items())
    expected = {
        "xor": {0: 30720, 14: 1920},
        "add": {0: 29056, **{n: 256 for n in range(1, 15)}},
        "zero_preserving_xor": {0: 30720, 14: 1800, 15: 120},
    }[kind]
    assert bijective and inverse_roundtrips
    assert dict(histogram) == expected
    assert fixed_zero == (kind == "zero_preserving_xor")
    assert self_inverse == (kind != "add")
    return {
        "bits": 8,
        "legal_local_ids": [1, limit],
        "key_count": size,
        "different_unordered_key_pairs": pairs,
        "bijective": bijective,
        "inverse_roundtrips": inverse_roundtrips,
        "fixed_zero": fixed_zero,
        "self_inverse": self_inverse,
        "overlap_histogram": dict(sorted(histogram.items())),
        "total_overlap": total_overlap,
        "mean_overlap": total_overlap / pairs,
        "wrong_key_valid_rate": total_overlap / (pairs * limit),
    }


def swap_zero_and_key(value, key):
    if value == 0:
        return key
    if value == key:
        return 0
    return value


def encode_reference(value, key):
    # Composition of XOR followed by an output transposition.
    return swap_zero_and_key(value ^ key, key)


def decode_reference(value, key):
    # Reverse the composition, rather than calling encode as its own oracle.
    return swap_zero_and_key(value, key) ^ key


def full_width_vectors():
    limit = 0xFFFFFF
    cases = [
        (1, 0, 0x01000000),
        (1, 1, 0xFF000001),
        (limit, limit, 0xFFFFFFFF),
        (limit, 0xFFFFFFFF, limit),
        (1, 0x01000000, 0),
        (0x123456, 0xAB123456, 0xCD123456),
        # Same high byte: explicit witnesses that the range check can pass.
        (1, 0xAB000000, 0xAB000002),
        (1, 0, 1),
    ]
    rng = random.Random(20261017)
    samples = []
    for _ in range(8192):
        local = rng.randrange(1, limit + 1)
        source = rng.getrandbits(32)
        target = rng.getrandbits(32)
        if source >> 24 == target >> 24:
            target ^= 0x01000000
        samples.append((local, source, target))
    vectors = []
    for index, (local, source, target) in enumerate(cases + samples):
        handle = encode_reference(local, source)
        decoded = decode_reference(handle, target)
        different_high_byte = source >> 24 != target >> 24
        assert handle != 0
        assert decode_reference(handle, source) == local
        assert encode_reference(0, source) == 0
        if different_high_byte:
            assert decoded > limit
        if index < len(cases):
            vectors.append({
                "local_id": f"0x{local:08X}",
                "source_key": f"0x{source:08X}",
                "target_key": f"0x{target:08X}",
                "encoded": f"0x{handle:08X}",
                "wrong_key_decoded": f"0x{decoded:08X}",
                "different_high_byte": different_high_byte,
                "passes_24bit_nonzero_range": 1 <= decoded <= limit,
            })
    return {
        "random_seed": 20261017,
        "different_high_byte_random_samples": len(samples),
        "samples_passed": True,
        "vectors": vectors,
        "limitation": "32bit checks are samples, not exhaustive proof or production-code verification.",
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    report = {
        "finite_set_witnesses": {
            kind: finite_witness(kind)
            for kind in ("xor", "add", "zero_preserving_xor")
        },
        "full_width_reference_vectors": full_width_vectors(),
    }
    result = json.dumps(report, indent=2) + "\n"
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(result, encoding="utf-8")
    else:
        print(result, end="")


if __name__ == "__main__":
    main()

using System.Text.Json;
using Atelia;
using Atelia.Data;
using Atelia.Rbf;
using Atelia.Rbf.Internal;

internal static class Program {
    // Fixed wire vectors are intentionally independent of the Python encoder and production serializer.
    private const string EmptyFrameHex = "5242463118000000000000006b4c6f70000000000b0000001800000052424631";
    private const string WrongForwardTrailerHex = "524246311800000000000000ad3fe435000000000b0000001800000052424631";
    private static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private static int Main(string[] args) {
        string? fixturesDirectory = null;
        bool assertBaseline = false;
        string? temporaryDirectory = null;
        try {
            for (int i = 0; i < args.Length; i++) {
                switch (args[i]) {
                    case "--verify-fixtures" when i + 1 < args.Length:
                        fixturesDirectory = Path.GetFullPath(args[++i]);
                        break;
                    case "--assert-baseline-5711c47":
                        assertBaseline = true;
                        break;
                    case "--help":
                        Console.WriteLine("ProductionProbe [--verify-fixtures <directory>] [--assert-baseline-5711c47]");
                        return 0;
                    default:
                        throw new ArgumentException($"Unknown or incomplete argument: {args[i]}");
                }
            }

            // Every generated file belongs to this fresh directory. Input fixture files are read-only.
            // Keep the directory for inspection; no recursive deletion or user-path cleanup is performed.
            temporaryDirectory = Path.Combine(Path.GetTempPath(), $"atelia-rbf-fast-open-{Guid.NewGuid():N}");
            Directory.CreateDirectory(temporaryDirectory);
            var result = new {
                schemaVersion = 1,
                probe = "production-rbf1",
                passed = true,
                temporaryDirectory,
                runtimeAssembly = typeof(RbfFile).Assembly.GetName().Name,
                fixedVectors = VerifyFixedVectors(temporaryDirectory),
                fixtureVerification = fixturesDirectory is null ? null : VerifyFixtures(fixturesDirectory, temporaryDirectory),
                openMetrics = new[] { 1, 100, 10000 }.Select(n => MeasureOpen(temporaryDirectory, n, assertBaseline)).ToArray(),
                historicalFenceTrace = HistoricalFenceTrace(temporaryDirectory),
                scope = "Current RBF1 production codec and read paths only; no RBF2 or mixed implementation, device-crash test, or latency benchmark."
            };
            Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
            return 0;
        }
        catch (Exception exception) {
            Console.WriteLine(JsonSerializer.Serialize(new {
                schemaVersion = 1,
                probe = "production-rbf1",
                passed = false,
                temporaryDirectory,
                exceptionType = exception.GetType().FullName,
                exception.Message
            }, JsonOptions));
            return 1;
        }
    }

    private static object VerifyFixedVectors(string temporaryDirectory) {
        string createdPath = Path.Combine(temporaryDirectory, "fixed-empty-created.rbf");
        SizedPtr ticket;
        using (var file = RbfFile.CreateNew(createdPath, RbfCacheMode.Off)) {
            ticket = file.Append(11, ReadOnlySpan<byte>.Empty).Unwrap();
            file.DurableFlush();
        }
        string actual = Hex(File.ReadAllBytes(createdPath));
        Require(actual == EmptyFrameHex, "Production empty/tag=11 wire differs from the fixed backward-CRC golden vector.");
        Require(ticket.Offset == 4 && ticket.Length == 24, "Fixed empty frame address changed.");

        string goldenPath = Path.Combine(temporaryDirectory, "fixed-empty-golden.rbf");
        File.WriteAllBytes(goldenPath, Convert.FromHexString(EmptyFrameHex));
        using (var file = RbfFile.OpenReadOnlyExisting(goldenPath, RbfCacheMode.Off)) {
            VerifyFrame(file, new FixtureFrame(4, 24, 11, "", "", false), "fixed-empty-golden");
        }

        string wrongPath = Path.Combine(temporaryDirectory, "wrong-forward-trailer.rbf");
        File.WriteAllBytes(wrongPath, Convert.FromHexString(WrongForwardTrailerHex));
        var rejection = StrictOpenRejection(wrongPath);
        Require(rejection.Rejected, "Production accepted the fixed forward-TrailerCRC negative vector.");

        var vectors = new[] {
            (Descriptor: 0u, Tag: 11u, Length: 64u, Hex: "77f637b5000000000b00000040000000"),
            (Descriptor: 0x80000000u, Tag: 0u, Length: 24u, Hex: "b4b81d2a000000800000000018000000"),
            (Descriptor: 0x60000005u, Tag: 0xDEADBEEFu, Length: 84u, Hex: "770322ea05000060efbeadde54000000")
        };
        Span<byte> trailer = stackalloc byte[16];
        foreach (var vector in vectors) {
            TrailerCodewordHelper.Serialize(trailer, vector.Descriptor, vector.Tag, vector.Length);
            Require(Hex(trailer) == vector.Hex, "Production Trailer serialization differs from a fixed backward-CRC vector.");
            Require(TrailerCodewordHelper.CheckTrailerCrc(Convert.FromHexString(vector.Hex)), "Production rejected a fixed Trailer vector.");
        }
        return new {
            emptyFrame = new { wireHex = actual, expectedWireHex = EmptyFrameHex, ticket.Offset, ticket.Length, tag = 11 },
            wrongForwardTrailer = new { wireHex = WrongForwardTrailerHex, rejection },
            backwardTrailerVectors = vectors.Select(v => new { v.Descriptor, v.Tag, v.Length, wireHex = v.Hex }).ToArray()
        };
    }

    private static FixtureVerification VerifyFixtures(string directory, string temporaryDirectory) {
        string manifestPath = Path.Combine(directory, "manifest.json");
        var manifest = JsonSerializer.Deserialize<FixtureManifest>(File.ReadAllText(manifestPath), JsonOptions)
            ?? throw new InvalidDataException("Fixture manifest is null.");
        Require(manifest.SchemaVersion == 1 && manifest.Profile == "RBF1", "Unsupported fixture manifest schema/profile.");
        Require(manifest.Fixtures is { Length: > 0 }, "Fixture manifest must contain at least one complete image.");
        var verified = new List<VerifiedFixture>();
        for (int i = 0; i < manifest.Fixtures.Length; i++) {
            Fixture fixture = manifest.Fixtures[i];
            Require(!string.IsNullOrWhiteSpace(fixture.File) && fixture.File == Path.GetFileName(fixture.File) && !Path.IsPathRooted(fixture.File),
                "Each fixture file must be a relative basename inside the fixture directory.");
            string path = Path.Combine(directory, fixture.File);
            byte[] wire = File.ReadAllBytes(path);
            Require(Hex(wire) == fixture.WireHex.ToLowerInvariant(), $"{fixture.Name}: input bytes differ from manifest wireHex.");
            using (var file = RbfFile.OpenReadOnlyExisting(path, RbfCacheMode.Off)) {
                Require(file.TailOffset == wire.Length, $"{fixture.Name}: strict Open changed the logical tail.");
                foreach (FixtureFrame frame in fixture.Frames) { VerifyFrame(file, frame, fixture.Name); }
            }

            string generatedPath = Path.Combine(temporaryDirectory, $"production-fixture-{i}.rbf");
            using (var file = RbfFile.CreateNew(generatedPath, RbfCacheMode.Off)) {
                foreach (FixtureFrame frame in fixture.Frames) {
                    Require(!frame.IsTombstone, "The bidirectional Append fixture set contains ordinary frames only.");
                    SizedPtr written = file.Append(frame.Tag, Convert.FromHexString(frame.PayloadHex), Convert.FromHexString(frame.MetaHex)).Unwrap();
                    Require(written.Offset == frame.Offset && written.Length == frame.Length, $"{fixture.Name}: production Append address differs.");
                }
                file.DurableFlush();
            }
            string productionWireHex = Hex(File.ReadAllBytes(generatedPath));
            Require(productionWireHex == fixture.WireHex.ToLowerInvariant(), $"{fixture.Name}: production writer and Python golden wire differ.");
            verified.Add(new VerifiedFixture(fixture.Name, fixture.File, fixture.Frames, productionWireHex, generatedPath));
        }
        return new FixtureVerification(Path.GetFullPath(directory), "Python complete image -> production strict reader; production writer emits complete wireHex for separate independent Python verification.", verified.ToArray());
    }

    private static void VerifyFrame(IRbfFile file, FixtureFrame expected, string name) {
        SizedPtr ticket = SizedPtr.Create(expected.Offset, expected.Length);
        using var frame = file.ReadPooledFrame(ticket).Unwrap();
        Require(frame.Ticket == ticket && frame.Tag == expected.Tag && frame.IsTombstone == expected.IsTombstone,
            $"{name}: production frame address/tag/tombstone differs.");
        int payloadLength = frame.PayloadAndMeta.Length - frame.TailMetaLength;
        Require(Hex(frame.PayloadAndMeta[..payloadLength]) == expected.PayloadHex.ToLowerInvariant(), $"{name}: payload bytes differ.");
        Require(Hex(frame.PayloadAndMeta[payloadLength..]) == expected.MetaHex.ToLowerInvariant(), $"{name}: TailMeta bytes differ.");
    }

    private static object MeasureOpen(string temporaryDirectory, int frameCount, bool assertBaseline) {
        string path = Path.Combine(temporaryDirectory, $"metrics-{frameCount}.rbf");
        using (var file = RbfFile.CreateNew(path, RbfCacheMode.Off)) {
            for (int i = 0; i < frameCount; i++) { file.Append(11, ReadOnlySpan<byte>.Empty).Unwrap(); }
            file.DurableFlush();
        }
        RbfReadMetricsSnapshot snapshot;
        using (var metrics = RbfReadMetrics.Begin()) {
            using var file = RbfFile.OpenReadOnlyExisting(path, RbfCacheMode.Off);
            snapshot = metrics.Snapshot();
        }
        long expectedCalls = 3L * frameCount + 5;
        long expectedBytes = 24L * frameCount + 32;
        bool matches = snapshot.ReadCalls == expectedCalls && snapshot.RequestedBytes == expectedBytes
            && snapshot.RawReadCalls == expectedCalls && snapshot.RawReturnedBytes == expectedBytes;
        if (assertBaseline) { Require(matches, $"N={frameCount}: current source no longer matches the 5711c47 empty-frame Open formula."); }
        return new {
            frameCount,
            cacheMode = "Off",
            operation = "OpenReadOnlyExisting only; input creation and later reads excluded",
            snapshot,
            expected5711c47 = new { readCalls = expectedCalls, requestedBytes = expectedBytes, formula = "3*N+5 calls; 24*N+32 bytes" },
            expectedFormulaMatches = matches,
            baselineAssertionEnabled = assertBaseline,
            metricScope = "Synchronous RBF read requests/returned bytes, including direct factory/recovery reads; not device physical I/O."
        };
    }

    private static object HistoricalFenceTrace(string temporaryDirectory) {
        string path = Path.Combine(temporaryDirectory, "damaged-historical-fence.rbf");
        SizedPtr first;
        byte[] payload = "historical A"u8.ToArray();
        byte[] meta = "meta A"u8.ToArray();
        using (var file = RbfFile.CreateNew(path, RbfCacheMode.Off)) {
            first = file.Append(11, payload, meta).Unwrap();
            file.Append(12, "middle B"u8).Unwrap();
            file.Append(13, "healthy tail C"u8).Unwrap();
            file.DurableFlush();
        }
        byte[] damaged = File.ReadAllBytes(path);
        damaged[checked((int)first.EndOffsetExclusive)] ^= 1;
        File.WriteAllBytes(path, damaged);
        var strict = StrictOpenRejection(path);
        Require(strict.Rejected, "Current strict Open did not reject the historical Fence corruption.");

        bool ordinaryFrameReadSucceeded;
        string? boundaryError;
        using (var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var facade = new RbfFileImpl(handle, damaged.Length, RbfCacheMode.Off, readOnly: true)) {
            var expected = new FixtureFrame(first.Offset, first.Length, 11, Hex(payload), Hex(meta), false);
            VerifyFrame(facade, expected, "unvalidated ordinary facade");
            ordinaryFrameReadSucceeded = true;
            var boundary = facade.GetScanBoundaryAfter(first);
            Require(boundary.IsFailure, "GetScanBoundaryAfter did not detect the historical Fence corruption.");
            boundaryError = boundary.Error!.ToString();
        }

        bool fixedCandidateFrameReadRejected;
        using (var candidate = RbfFile.OpenReadOnlyCandidate(path, damaged.Length, RbfCacheMode.Off)) {
            var read = candidate.ReadPooledFrame(first);
            fixedCandidateFrameReadRejected = read.IsFailure;
            if (read.IsSuccess) { read.Value!.Dispose(); }
            Require(fixedCandidateFrameReadRejected, "The fixed-EOF offline candidate did not enforce its stronger ticket Fence guard.");
        }
        return new {
            firstFrame = new { first.Offset, first.Length },
            corruptedFenceOffset = first.EndOffsetExclusive,
            strictOpen = strict,
            unvalidatedOrdinaryFacade = new {
                ordinaryFrameReadSucceeded,
                getScanBoundaryAfterRejected = true,
                boundaryError,
                meaning = "Constructing RbfFileImpl directly bypasses today's eager Open checks to isolate existing ordinary read behavior; this is not a proposed fast-Open implementation."
            },
            fixedEofOfflineCandidate = new {
                frameReadRejected = fixedCandidateFrameReadRejected,
                meaning = "The separate offline candidate guards a ticket's trailing Fence; its stronger result must not be attributed to ordinary checked-read."
            }
        };
    }

    private static OpenRejection StrictOpenRejection(string path) {
        try {
            using var file = RbfFile.OpenReadOnlyExisting(path, RbfCacheMode.Off);
            return new OpenRejection(false, null, null);
        }
        catch (InvalidDataException exception) {
            return new OpenRejection(true, exception.GetType().FullName, exception.Message);
        }
    }

    private static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
    private static void Require(bool condition, string message) {
        if (!condition) { throw new InvalidDataException(message); }
    }

    private sealed record FixtureManifest(int SchemaVersion, string Profile, Fixture[] Fixtures);
    private sealed record Fixture(string Name, string File, string WireHex, FixtureFrame[] Frames);
    private sealed record FixtureFrame(long Offset, int Length, uint Tag, string PayloadHex, string MetaHex, bool IsTombstone);
    private sealed record FixtureVerification(string Directory, string EvidenceDirections, VerifiedFixture[] Fixtures);
    private sealed record VerifiedFixture(string Name, string InputFile, FixtureFrame[] Frames, string WireHex, string ProductionFile);
    private sealed record OpenRejection(bool Rejected, string? ExceptionType, string? Message);
}

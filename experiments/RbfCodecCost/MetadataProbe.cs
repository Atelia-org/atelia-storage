using System.Buffers.Binary;
using Atelia.Data.Hashing;
using Atelia.Rbf;
using Atelia.Rbf.ReadCache;

namespace RbfCodecCost;

internal sealed record MetadataMeasurement(string Workload, string Operation, Measurement Timing,
    long ReadsIn100Operations, long RequestedBytesIn100Operations, long RawCallsIn100Operations,
    long RawBytesIn100Operations, bool WireCacheUnmodified);

internal static class MetadataProbe {
    public static List<MetadataMeasurement> Run(string output, int samples) {
        var rows = new List<MetadataMeasurement>();
        foreach (int metaLength in new[] { 0, 1, 3, 65535 }) {
            int payloadLength = 1048577;
            byte[] combined = Corpus.Create(payloadLength + metaLength, "dense301");
            var prepared = PrototypeCodec.Prepare([combined], metaLength, 11, KeyStrategy.ZeroFirst);
            string path = Path.Combine(output, $"metadata-{metaLength}.rbf");
            using (var writer = File.OpenHandle(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None)) {
                byte[] header = BitConverter.GetBytes(PrototypeCodec.Fence); RandomAccess.Write(writer, header, 0);
                long offset = 4; new StreamCodec(new byte[65536]).Append(writer, prepared, ref offset);
                RandomAccess.FlushToDisk(writer);
            }
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
            foreach (bool cached in new[] { false, true }) {
                using RandomAccessReader reader = cached ? new ReverseReadCache(handle) : new RandomAccessReader(handle);
                string workload = $"meta-{metaLength}/cache-{(cached ? "Slots16" : "Off")}/key-{prepared.Key}/phase1";
                byte[] metaDestination = new byte[metaLength];
                Func<ulong> infoAction = () => ReadInfo(reader, prepared.FrameLength);
                Func<ulong> metaAction = () => {
                    if (metaLength == 0) { reader.EnsureUsable(); return 0; }
                    if (reader.Read(metaDestination, 8L + payloadLength) != metaLength) throw new EndOfStreamException();
                    XorTransform.InPlace(metaDestination, prepared.Key, payloadLength);
                    return metaDestination[0];
                };
                foreach (var operation in new[] { (Name: "FrameInfo20", Action: infoAction), (Name: "TailMeta-from-info", Action: metaAction) }) {
                    Measurement timing = Timing.Measure(workload, operation.Name, operation.Name == "FrameInfo20" ? 20 : metaLength,
                        1, prepared.Key, operation.Action, samples);
                    using var metrics = RbfReadMetrics.Begin();
                    for (int i = 0; i < 100; i++) operation.Action();
                    var snapshot = metrics.Snapshot();
                    if (operation.Name != "FrameInfo20") Correctness.Require(metaDestination.AsSpan().SequenceEqual(combined.AsSpan(payloadLength)), "Meta phase original bytes");
                    // Re-read raw wire after many cached decode operations; cache pages must remain encoded.
                    byte[] raw = new byte[20], cachedBytes = new byte[20];
                    long tailOffset = 4L + prepared.FrameLength - 20;
                    StreamCodec.ReadExactly(handle, raw, tailOffset); reader.Read(cachedBytes, tailOffset);
                    Correctness.Require(raw.AsSpan().SequenceEqual(cachedBytes), "Encoded cache remains unmodified");
                    if (metaLength > 0) {
                        byte[] rawMeta = new byte[metaLength], cacheMeta = new byte[metaLength];
                        StreamCodec.ReadExactly(handle, rawMeta, 8L + payloadLength);
                        reader.Read(cacheMeta, 8L + payloadLength);
                        Correctness.Require(rawMeta.AsSpan().SequenceEqual(cacheMeta), "Encoded meta cache remains unmodified");
                    }
                    rows.Add(new(workload, operation.Name, timing, snapshot.ReadCalls, snapshot.RequestedBytes,
                        snapshot.RawReadCalls, snapshot.RawReturnedBytes, true));
                }
            }
        }
        return rows;
    }

    private static uint ReadInfo(RandomAccessReader reader, int frameLength) {
        Span<byte> trailerKey = stackalloc byte[20];
        if (reader.Read(trailerKey, 4L + frameLength - 20) != 20) throw new EndOfStreamException();
        uint key = BinaryPrimitives.ReadUInt32LittleEndian(trailerKey[16..]);
        if (key > (frameLength - 8) / 4) throw new InvalidDataException("Key");
        XorTransform.InPlace(trailerKey[..16], key, frameLength - 24);
        if (RollingCrc.CrcBackward(trailerKey[4..16]) != BinaryPrimitives.ReadUInt32BigEndian(trailerKey)) throw new InvalidDataException("TrailerCRC");
        if (BinaryPrimitives.ReadUInt32LittleEndian(trailerKey[12..]) != frameLength) throw new InvalidDataException("TailLen");
        uint descriptor = BinaryPrimitives.ReadUInt32LittleEndian(trailerKey[4..]);
        int coverage = frameLength - 28, padding = (int)((descriptor >> 29) & 3), meta = (int)(descriptor & 65535);
        if ((descriptor & 0x1FFF0000) != 0 || padding > coverage || meta > coverage - padding) throw new InvalidDataException("Descriptor");
        return key;
    }
}

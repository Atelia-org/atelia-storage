using System.Buffers;
using System.Buffers.Binary;
using Atelia.Data;
using Xunit;

namespace Atelia.Rbf.Internal.Tests;

public sealed class RbfSizedAppendAcceptanceTests : IDisposable {
    private const int EncodedTicketLength = sizeof(long) + sizeof(int);
    private readonly string _pathA = Path.Combine(Path.GetTempPath(), $"rbf-sized-a-{Guid.NewGuid():N}.rbf");
    private readonly string _pathB = Path.Combine(Path.GetTempPath(), $"rbf-sized-b-{Guid.NewGuid():N}.rbf");

    public void Dispose() {
        File.Delete(_pathA);
        File.Delete(_pathB);
    }

    [Fact]
    public void TwoFiles_PreissueAndStoreMutualReferences_ColdReopenPreservesThem() {
        SizedPtr ticketA;
        SizedPtr ticketB;
        using (IRbfFile fileA = RbfFile.CreateNew(_pathA))
        using (IRbfFile fileB = RbfFile.CreateNew(_pathB)) {
            fileA.Append(1, new byte[] { 0xA1 }).Unwrap();
            fileB.Append(2, new byte[] { 0xB1, 0xB2, 0xB3, 0xB4, 0xB5 }).Unwrap();

            long oldTailA = fileA.TailOffset;
            long oldTailB = fileB.TailOffset;
            using var builderA = fileA.BeginAppend(EncodedTicketLength, 0, out ticketA);
            using var builderB = fileB.BeginAppend(EncodedTicketLength, 0, out ticketB);

            Assert.Equal(oldTailA, fileA.TailOffset);
            Assert.Equal(oldTailB, fileB.TailOffset);
            Assert.NotEqual(ticketA, ticketB);
            Assert.Throws<InvalidOperationException>(() => fileA.ReadFrame(ticketA, new byte[ticketA.Length]));
            Assert.Throws<InvalidOperationException>(() => fileB.ReadFrame(ticketB, new byte[ticketB.Length]));

            builderA.PayloadAndMeta.Write(EncodeTicket(ticketB));
            builderB.PayloadAndMeta.Write(EncodeTicket(ticketA));
            Assert.Equal(ticketA, builderA.EndAppend(101).Unwrap());
            Assert.Equal(ticketB, builderB.EndAppend(102).Unwrap());

            using var frameA = fileA.ReadPooledFrame(ticketA).Unwrap();
            using var frameB = fileB.ReadPooledFrame(ticketB).Unwrap();
            Assert.Equal(ticketB, DecodeTicket(frameA.PayloadAndMeta));
            Assert.Equal(ticketA, DecodeTicket(frameB.PayloadAndMeta));
        }

        using (IRbfFile reopenedA = RbfFile.OpenReadOnlyExisting(_pathA))
        using (IRbfFile reopenedB = RbfFile.OpenReadOnlyExisting(_pathB)) {
            using var frameA = reopenedA.ReadPooledFrame(ticketA).Unwrap();
            using var frameB = reopenedB.ReadPooledFrame(ticketB).Unwrap();
            Assert.Equal(ticketB, DecodeTicket(frameA.PayloadAndMeta));
            Assert.Equal(ticketA, DecodeTicket(frameB.PayloadAndMeta));
        }
    }

    [Fact]
    public void CancelledTicket_IsNotReadableUntilALaterAppendReusesItsAddress() {
        SizedPtr ticketA;
        SizedPtr referenceFrameA;
        SizedPtr ticketB;
        using (IRbfFile fileA = RbfFile.CreateNew(_pathA)) {
            ticketA = fileA.Append(201, new byte[] { 0xC1, 0xC2 }).Unwrap();
            using (RbfFile.CreateNew(_pathB)) { }
            byte[] originalImageB = File.ReadAllBytes(_pathB);

            using (IRbfFile fileB = RbfFile.OpenExisting(_pathB, out _, RbfCacheMode.Off)) {
                var builderB = fileB.BeginAppend(EncodedTicketLength, 0, out ticketB);

                // A's completed bytes keep B's early address even when B is cancelled.
                referenceFrameA = fileA.Append(202, EncodeTicket(ticketB)).Unwrap();
                using var readA = fileA.ReadPooledFrame(referenceFrameA).Unwrap();
                Assert.Equal(ticketB, DecodeTicket(readA.PayloadAndMeta));

                Assert.Equal(ticketB.Offset, fileB.TailOffset);
                builderB.Dispose();
            }

            Assert.Equal(originalImageB, File.ReadAllBytes(_pathB));
        }

        using (IRbfFile fileB = RbfFile.OpenExisting(_pathB, out _, RbfCacheMode.Off)) {
            Assert.True(fileB.ReadFrame(ticketB, new byte[ticketB.Length]).IsFailure);
            using var builderC = fileB.BeginAppend(EncodedTicketLength, 0, out var ticketC);
            Assert.Equal(ticketB, ticketC);
            builderC.PayloadAndMeta.Write(EncodeTicket(ticketA));
            Assert.Equal(ticketC, builderC.EndAppend(203).Unwrap());

            // SizedPtr identifies an address, not a cancelled attempt generation.
            using var readThroughOldTicket = fileB.ReadPooledFrame(ticketB).Unwrap();
            Assert.Equal(ticketA, DecodeTicket(readThroughOldTicket.PayloadAndMeta));
        }

        using (IRbfFile coldA = RbfFile.OpenReadOnlyExisting(_pathA))
        using (IRbfFile coldB = RbfFile.OpenReadOnlyExisting(_pathB)) {
            using var readA = coldA.ReadPooledFrame(referenceFrameA).Unwrap();
            using var originalA = coldA.ReadPooledFrame(ticketA).Unwrap();
            using var readC = coldB.ReadPooledFrame(ticketB).Unwrap();
            Assert.Equal(ticketB, DecodeTicket(readA.PayloadAndMeta));
            Assert.Equal(new byte[] { 0xC1, 0xC2 }, originalA.PayloadAndMeta.ToArray());
            Assert.Equal(ticketA, DecodeTicket(readC.PayloadAndMeta));
        }
    }

    private static byte[] EncodeTicket(SizedPtr ticket) {
        byte[] bytes = new byte[EncodedTicketLength];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, ticket.Offset);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(sizeof(long)), ticket.Length);
        return bytes;
    }

    private static SizedPtr DecodeTicket(ReadOnlySpan<byte> bytes) {
        Assert.Equal(EncodedTicketLength, bytes.Length);
        return SizedPtr.Create(
            BinaryPrimitives.ReadInt64LittleEndian(bytes),
            BinaryPrimitives.ReadInt32LittleEndian(bytes[sizeof(long)..]));
    }
}

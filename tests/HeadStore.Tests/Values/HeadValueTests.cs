using System.Reflection;
using Atelia.Data;
using Atelia.HeadStore.Internal.Format;
using Atelia.HeadStore.Tests.Format;
using Xunit;

namespace Atelia.HeadStore.Tests.Values;

public class HeadValueTests {
    [Fact]
    public void HeadId_EqualityIncludesEveryIdentityBitAndFullLocalValue() {
        byte[] originalBytes = Convert.FromHexString(FormatTestValues.IdentityHex);
        Assert.True(StoreIdentity.TryRead(originalBytes, out StoreIdentity identity));
        HeadId original = HeadId.Create(identity, 0x89ABCDEF);
        Assert.Equal(0x89ABCDEFu, original.LocalId);
        Array.Clear(originalBytes);
        Assert.True(original.BelongsTo(FormatTestValues.Identity()));
        Assert.Equal(original, HeadId.Create(FormatTestValues.Identity(), 0x89ABCDEF));
        Assert.NotEqual(original, HeadId.Create(identity, 0x09ABCDEF));

        byte[] canonical = Convert.FromHexString(FormatTestValues.IdentityHex);
        for (int bit = 0; bit < 128; bit++) {
            byte[] changed = canonical.ToArray();
            changed[bit / 8] ^= (byte)(1 << (bit % 8));
            Assert.True(StoreIdentity.TryRead(changed, out StoreIdentity foreignIdentity));
            HeadId foreign = HeadId.Create(foreignIdentity, original.LocalId);
            Assert.NotEqual(original, foreign);
            Assert.False(original.BelongsTo(foreignIdentity));
        }
    }

    [Fact]
    public void HeadId_SamePersistedIdentityReconstructionHasStableValueEqualityAndHash() {
        StoreIdentity first = FormatTestValues.Identity();
        byte[] persisted = Convert.FromHexString(FormatTestValues.IdentityHex);
        Assert.True(StoreIdentity.TryRead(persisted, out StoreIdentity reopened));
        HeadId left = HeadId.Create(first, uint.MaxValue);
        HeadId right = HeadId.Create(reopened, uint.MaxValue);
        Assert.True(left == right);
        Assert.False(left != right);
        Assert.True(left.Equals((object)right));
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
        Assert.False(left.Equals(null));
        Assert.False(left.Equals(left.LocalId));
        Assert.Single(new HashSet<HeadId> { left, right });
    }

    [Fact]
    public void HeadId_DefaultSupportsPureValuesButBelongsToNoValidContext() {
        HeadId left = default;
        HeadId right = default;
        Assert.Equal(0u, left.LocalId);
        Assert.True(left.Equals(right));
        Assert.True(left == right);
        Assert.False(left != right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
        Assert.False(left.BelongsTo(default));
        Assert.False(left.BelongsTo(FormatTestValues.Identity()));
        Assert.False(HeadId.Create(FormatTestValues.Identity(), 1).BelongsTo(default));
    }

    [Fact]
    public void HeadEntryId_EqualityIncludesHeadIdentityOffsetAndLength() {
        StoreIdentity identity = FormatTestValues.Identity();
        HeadId headId = HeadId.Create(identity, 7);
        SizedPtr ticket = SizedPtr.Create(60, 40);
        HeadEntryId entryId = HeadEntryId.Create(headId, ticket);
        HeadEntryId same = HeadEntryId.Create(HeadId.Create(FormatTestValues.Identity(), 7), SizedPtr.FromPacked(ticket.Packed));
        Assert.Equal(headId, entryId.HeadId);
        Assert.Equal(ticket, entryId.Ticket);
        Assert.True(entryId == same);
        Assert.False(entryId != same);
        Assert.Equal(entryId.GetHashCode(), same.GetHashCode());
        Assert.True(entryId.Equals((object)same));
        Assert.False(entryId.Equals(null));
        Assert.False(entryId.Equals(ticket));
        Assert.NotEqual(entryId, HeadEntryId.Create(headId, SizedPtr.Create(60, 44)));
        Assert.NotEqual(entryId, HeadEntryId.Create(headId, SizedPtr.Create(104, 40)));
        Assert.NotEqual(entryId, HeadEntryId.Create(HeadId.Create(identity, 8), ticket));
        Assert.NotEqual(entryId, HeadEntryId.Create(HeadId.Create(FormatTestValues.DataIdentity(), 7), ticket));
        Assert.Single(new HashSet<HeadEntryId> { entryId, same });
    }

    [Fact]
    public void HeadEntryId_DefaultPropertiesEqualityAndHashRemainUsable() {
        HeadEntryId left = default;
        HeadEntryId right = default;
        Assert.Equal(default, left.HeadId);
        Assert.Equal(default, left.Ticket);
        Assert.True(left == right);
        Assert.False(left != right);
        Assert.True(left.Equals((object)right));
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }

    [Fact]
    public void Values_PublicSurfaceHasOnlyReadonlyProjectionsAndNoPositionConstructorsOrCodecs() {
        AssertOpaqueValueSurface(typeof(HeadId), "LocalId");
        AssertOpaqueValueSurface(typeof(HeadEntryId), "HeadId", "Ticket");
        Assert.Equal(typeof(uint), typeof(HeadId).GetProperty("LocalId")!.PropertyType);
        Assert.Equal(typeof(HeadId), typeof(HeadEntryId).GetProperty("HeadId")!.PropertyType);
        Assert.Equal(typeof(SizedPtr), typeof(HeadEntryId).GetProperty("Ticket")!.PropertyType);
    }

    [Fact]
    public void PublicationOutcome_IsTheSpecifiedNonpersistentThreeStateEvidence() {
        Assert.Equal(0, (int)PublicationOutcome.NotAttempted);
        Assert.Equal(1, (int)PublicationOutcome.Unknown);
        Assert.Equal(2, (int)PublicationOutcome.Confirmed);
        Assert.Equal(new[] { "NotAttempted", "Unknown", "Confirmed" }, Enum.GetNames<PublicationOutcome>());
    }

    private static void AssertOpaqueValueSurface(Type type, params string[] expectedProperties) {
        Assert.True(type.IsValueType);
        Assert.Empty(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.Empty(type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static));
        PropertyInfo[] properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        Assert.Equal(expectedProperties.OrderBy(name => name), properties.Select(property => property.Name).OrderBy(name => name));
        Assert.All(properties, property => Assert.Null(property.SetMethod));
        foreach (string absent in new[] { "Deconstruct", "IsValid", "TryRead", "TryWrite", "Parse", "CompareTo" }) {
            Assert.Null(type.GetMethod(absent, BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static));
        }

        Assert.False(typeof(IDisposable).IsAssignableFrom(type));
    }
}

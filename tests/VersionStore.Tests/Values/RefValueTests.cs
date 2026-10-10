using System.Reflection;
using Atelia.Data;
using Atelia.VersionStore.Internal.Format;
using Atelia.VersionStore.Tests.Format;
using Xunit;

namespace Atelia.VersionStore.Tests.Values;

public class RefValueTests {
    [Fact]
    public void RefId_EqualityIncludesEveryIdentityBitAndFullLocalValue() {
        byte[] originalBytes = Convert.FromHexString(FormatTestValues.IdentityHex);
        Assert.True(StoreIdentity.TryRead(originalBytes, out StoreIdentity identity));
        RefId original = RefId.Create(identity, 0x89ABCDEF);
        Assert.Equal(0x89ABCDEFu, original.LocalId);
        Array.Clear(originalBytes);
        Assert.True(original.BelongsTo(FormatTestValues.Identity()));
        Assert.Equal(original, RefId.Create(FormatTestValues.Identity(), 0x89ABCDEF));
        Assert.NotEqual(original, RefId.Create(identity, 0x09ABCDEF));

        byte[] canonical = Convert.FromHexString(FormatTestValues.IdentityHex);
        for (int bit = 0; bit < 128; bit++) {
            byte[] changed = canonical.ToArray();
            changed[bit / 8] ^= (byte)(1 << (bit % 8));
            Assert.True(StoreIdentity.TryRead(changed, out StoreIdentity foreignIdentity));
            RefId foreign = RefId.Create(foreignIdentity, original.LocalId);
            Assert.NotEqual(original, foreign);
            Assert.False(original.BelongsTo(foreignIdentity));
        }
    }

    [Fact]
    public void RefId_SamePersistedIdentityReconstructionHasStableValueEqualityAndHash() {
        StoreIdentity first = FormatTestValues.Identity();
        byte[] persisted = Convert.FromHexString(FormatTestValues.IdentityHex);
        Assert.True(StoreIdentity.TryRead(persisted, out StoreIdentity reopened));
        RefId left = RefId.Create(first, uint.MaxValue);
        RefId right = RefId.Create(reopened, uint.MaxValue);
        Assert.True(left == right);
        Assert.False(left != right);
        Assert.True(left.Equals((object)right));
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
        Assert.False(left.Equals(null));
        Assert.False(left.Equals(left.LocalId));
        Assert.Single(new HashSet<RefId> { left, right });
    }

    [Fact]
    public void RefId_DefaultSupportsPureValuesButBelongsToNoValidContext() {
        RefId left = default;
        RefId right = default;
        Assert.Equal(0u, left.LocalId);
        Assert.True(left.Equals(right));
        Assert.True(left == right);
        Assert.False(left != right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
        Assert.False(left.BelongsTo(default));
        Assert.False(left.BelongsTo(FormatTestValues.Identity()));
        Assert.False(RefId.Create(FormatTestValues.Identity(), 1).BelongsTo(default));
    }

    [Fact]
    public void RefRevision_EqualityIncludesRefIdentityOffsetAndLength() {
        StoreIdentity identity = FormatTestValues.Identity();
        RefId refId = RefId.Create(identity, 7);
        SizedPtr ticket = SizedPtr.Create(60, 40);
        RefRevision revision = RefRevision.Create(refId, ticket);
        RefRevision same = RefRevision.Create(RefId.Create(FormatTestValues.Identity(), 7), SizedPtr.FromPacked(ticket.Packed));
        Assert.Equal(refId, revision.RefId);
        Assert.Equal(ticket, revision.Ticket);
        Assert.True(revision == same);
        Assert.False(revision != same);
        Assert.Equal(revision.GetHashCode(), same.GetHashCode());
        Assert.True(revision.Equals((object)same));
        Assert.False(revision.Equals(null));
        Assert.False(revision.Equals(ticket));
        Assert.NotEqual(revision, RefRevision.Create(refId, SizedPtr.Create(60, 44)));
        Assert.NotEqual(revision, RefRevision.Create(refId, SizedPtr.Create(104, 40)));
        Assert.NotEqual(revision, RefRevision.Create(RefId.Create(identity, 8), ticket));
        Assert.NotEqual(revision, RefRevision.Create(RefId.Create(FormatTestValues.DataIdentity(), 7), ticket));
        Assert.Single(new HashSet<RefRevision> { revision, same });
    }

    [Fact]
    public void RefRevision_DefaultPropertiesEqualityAndHashRemainUsable() {
        RefRevision left = default;
        RefRevision right = default;
        Assert.Equal(default, left.RefId);
        Assert.Equal(default, left.Ticket);
        Assert.True(left == right);
        Assert.False(left != right);
        Assert.True(left.Equals((object)right));
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }

    [Fact]
    public void Values_PublicSurfaceHasOnlyReadonlyProjectionsAndNoPositionConstructorsOrCodecs() {
        AssertOpaqueValueSurface(typeof(RefId), "LocalId");
        AssertOpaqueValueSurface(typeof(RefRevision), "RefId", "Ticket");
        Assert.Equal(typeof(uint), typeof(RefId).GetProperty("LocalId")!.PropertyType);
        Assert.Equal(typeof(RefId), typeof(RefRevision).GetProperty("RefId")!.PropertyType);
        Assert.Equal(typeof(SizedPtr), typeof(RefRevision).GetProperty("Ticket")!.PropertyType);
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

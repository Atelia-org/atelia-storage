using System.Collections;
using System.Reflection;
using Atelia.Data;
using Xunit;

namespace Atelia.HeadStore.Tests.Tags;

public sealed class HeadSetTagTests {
    [Fact]
    public void OwnedContainerExposesNoWritableCollectionInterface() {
        var id = HeadId.Create(TagTestWire.Identity, 1);
        var entry = HeadEntryId.Create(id, SizedPtr.Create(60, 40));
        var tag = new HeadSetTag("A", -1, new() { [id] = entry });
        Assert.Equal(entry, tag.Entries[id]);
        Assert.False(tag.Entries is IDictionary<HeadId, HeadEntryId>);
        Assert.False(tag.Entries is IDictionary);
        Assert.False(tag.Entries.Keys is ICollection<HeadId>);
        Assert.False(tag.Entries.Values is ICollection<HeadEntryId>);
        Assert.Equal(id, Assert.Single(tag.Entries).Key);
    }

    [Fact]
    public void PublicSurfaceHasOnlyOwnedNameTimeAndReadOnlyEntries() {
        Type type = typeof(HeadSetTag);
        Assert.True(type.IsSealed);
        Assert.False(typeof(IDisposable).IsAssignableFrom(type));
        Assert.Empty(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.Empty(type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static));
        PropertyInfo[] properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        Assert.Equal(new[] { "Entries", "Name", "RecordedAtUnixMs" }, properties.Select(property => property.Name).OrderBy(name => name));
        Assert.All(properties, property => Assert.Null(property.SetMethod));
        Assert.Equal(typeof(IReadOnlyDictionary<HeadId, HeadEntryId>), type.GetProperty("Entries")!.PropertyType);
    }
}

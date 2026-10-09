using System.Collections;
using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Text.Json;

// BCL ownership probe; int is an immutable stand-in, not a FrameAddress codec.
var observations = new List<object>();
foreach (int count in new[] { 0, 1, 4, 100 }) {
    foreach (bool frozen in new[] { false, true }) {
        foreach (string view in new[] { "map", "keys", "values" }) {
            var backing = Enumerable.Range(0, count).ToDictionary(i => $"k{i}", i => i, StringComparer.Ordinal);
            IReadOnlyDictionary<string, int> map = frozen
                ? backing.ToFrozenDictionary(StringComparer.Ordinal)
                : new ReadOnlyDictionary<string, int>(backing);
            object surface = view switch { "keys" => map.Keys, "values" => map.Values, _ => map };
            object? syncRoot = null;
            string? syncRootError = null;
            if (surface is ICollection collection) {
                try { syncRoot = collection.SyncRoot; }
                catch (NotSupportedException error) { syncRootError = error.GetType().Name; }
            }
            bool mutableRoot = syncRoot is Dictionary<string, int>;
            if (syncRoot is Dictionary<string, int> escaped) { escaped["escaped"] = 7; }
            bool rootMutationVisible = map.ContainsKey("escaped");
            bool directMutationSucceeded = false;
            try {
                if (surface is IDictionary<string, int> generic) {
                    generic["direct"] = 8;
                    directMutationSucceeded = true;
                } else if (surface is IDictionary nongeneric) {
                    nongeneric["direct"] = 8;
                    directMutationSucceeded = true;
                }
            } catch (NotSupportedException) { }
            if (frozen) { Require(!mutableRoot && !rootMutationVisible && !directMutationSucceeded); }
            observations.Add(new {
                Kind = frozen ? "FrozenDictionary" : "ReadOnlyDictionary",
                Count = count, View = view, SurfaceType = surface.GetType().FullName,
                SyncRootType = syncRoot?.GetType().FullName, SyncRootError = syncRootError,
                MutableRoot = mutableRoot, RootMutationVisible = rootMutationVisible,
                DirectMutationSucceeded = directMutationSucceeded
            });
        }
    }
}

var input = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["A"] = 1 };
var owned = FreezeChecked(input);
input["A"] = 2;
input["later"] = 3;
bool detachedAndOrdinal = owned["A"] == 1 && !owned.ContainsKey("a") && !owned.ContainsKey("later");
Require(detachedAndOrdinal);
var duplicates = new[] { KeyValuePair.Create("x", 1), KeyValuePair.Create("x", 2) };
bool directFrozenKeepsLastDuplicate = duplicates.ToFrozenDictionary(StringComparer.Ordinal)["x"] == 2;
bool checkedCopyRejectsDuplicate = false;
try { _ = FreezeChecked(duplicates); } catch (ArgumentException) { checkedCopyRejectsDuplicate = true; }
Require(checkedCopyRejectsDuplicate);
var left = FreezeChecked(new[] { KeyValuePair.Create("A", 1), KeyValuePair.Create("a", 2) });
var right = FreezeChecked(new[] { KeyValuePair.Create("a", 2), KeyValuePair.Create("A", 1) });
var different = FreezeChecked(new[] { KeyValuePair.Create("A", 1), KeyValuePair.Create("a", 3) });
bool contentEqualityIgnoresOrder = SameContents(left, right) && !SameContents(left, different);
Require(contentEqualityIgnoresOrder);

Console.WriteLine(JsonSerializer.Serialize(new {
    Runtime = Environment.Version.ToString(), Platform = RuntimeInformation.OSDescription,
    DetachedAndOrdinal = detachedAndOrdinal, DirectFrozenKeepsLastDuplicate = directFrozenKeepsLastDuplicate,
    CheckedCopyRejectsDuplicate = checkedCopyRejectsDuplicate,
    ContentEqualityIgnoresOrder = contentEqualityIgnoresOrder, Observations = observations
}, new JsonSerializerOptions { WriteIndented = true }));

static FrozenDictionary<string, int> FreezeChecked(IEnumerable<KeyValuePair<string, int>> input) {
    var copy = new Dictionary<string, int>(StringComparer.Ordinal);
    foreach (var pair in input) {
        if (!copy.TryAdd(pair.Key, pair.Value)) { throw new ArgumentException("Duplicate Ordinal key."); }
    }
    return copy.ToFrozenDictionary(StringComparer.Ordinal);
}

static bool SameContents(IReadOnlyDictionary<string, int> left, IReadOnlyDictionary<string, int> right) =>
    left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out int value) && value == pair.Value);

static void Require(bool condition) {
    if (!condition) { throw new InvalidOperationException("BCL probe expectation failed."); }
}

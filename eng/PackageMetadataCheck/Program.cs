using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;

if (args.Length != 4) { throw new ArgumentException("Expected manifest, source root, assets file, expected version."); }
string manifestPath = Path.GetFullPath(args[0]);
string feed = Path.GetDirectoryName(manifestPath)!;
string sourceRoot = Path.GetFullPath(args[1]);
using var manifestJson = JsonDocument.Parse(File.ReadAllText(manifestPath));
var manifest = manifestJson.RootElement;
string revision = manifest.GetProperty("sourceRevision").GetString()!;
string version = manifest.GetProperty("version").GetString()!;
Require(version == args[3], "Manifest version differs from requested version.");
string repository = manifest.GetProperty("repositoryUrl").GetString()!;
string sourcePrefix = repository.Replace("https://github.com/", "https://raw.githubusercontent.com/", StringComparison.Ordinal) + "/" + revision + "/";
var expected = manifest.GetProperty("packages").EnumerateArray().Select(p => p.GetProperty("id").GetString()!).ToHashSet(StringComparer.OrdinalIgnoreCase);
Require(expected.Count == 5, "Expected five distinct storage packages.");
int checkedSourceDocuments = 0;
foreach (var package in manifest.GetProperty("packages").EnumerateArray()) {
    string id = package.GetProperty("id").GetString()!;
    using var archive = OpenVerifiedArchive(package, "file", "sha256", "nupkg");
    using var symbols = OpenVerifiedArchive(package, "symbolsFile", "symbolsSha256", "snupkg");
    using var nuspecStream = archive.Entries.Single(e => e.FullName.EndsWith(".nuspec", StringComparison.Ordinal)).Open();
    var metadata = XDocument.Load(nuspecStream).Root!.Elements().Single(e => e.Name.LocalName == "metadata");
    string Value(string name) => metadata.Elements().Single(e => e.Name.LocalName == name).Value;
    Require(Value("id") == id && Value("version") == version, $"{id}: nuspec identity mismatch.");
    Require(Value("license") == "MIT" && Value("readme") == "README.md", $"{id}: missing MIT/README metadata.");
    var origin = metadata.Elements().Single(e => e.Name.LocalName == "repository");
    Require((string?)origin.Attribute("commit") == revision && (string?)origin.Attribute("url") == repository && (string?)origin.Attribute("type") == "git", $"{id}: repository metadata mismatch.");
    foreach (string asset in new[] { "LICENSE", "README.md", $"lib/net10.0/{id}.dll", $"lib/net10.0/{id}.xml" }) {
        Require(archive.GetEntry(asset) is { Length: > 0 }, $"{id}: missing {asset}.");
    }
    using (var assemblyBytes = new MemoryStream()) {
        using (var stream = archive.GetEntry($"lib/net10.0/{id}.dll")!.Open()) { stream.CopyTo(assemblyBytes); }
        assemblyBytes.Position = 0;
        using var pe = new PEReader(assemblyBytes);
        var assemblyMetadata = pe.GetMetadataReader();
        var definition = assemblyMetadata.GetAssemblyDefinition();
        Require(assemblyMetadata.GetString(definition.Name) == id && definition.Version == new Version(1, 0, 0, 0), $"{id}: extraction changed assembly identity/version.");
    }
    foreach (var dependency in metadata.Descendants().Where(e => e.Name.LocalName == "dependency")) {
        string dependencyId = (string)dependency.Attribute("id")!;
        if (expected.Contains(dependencyId)) {
            string dependencyVersion = (string)dependency.Attribute("version")!;
            Require(dependencyVersion == version || dependencyVersion == $"[{version}, )" || dependencyVersion == $"[{version},)" || dependencyVersion == $"[{version}]", $"{id}: unexpected storage dependency {dependencyId} {dependencyVersion}.");
        }
    }
    var pdbEntry = symbols.GetEntry($"lib/net10.0/{id}.pdb") ?? throw new InvalidDataException($"{id}: portable PDB missing.");
    using var pdbBytes = new MemoryStream();
    using (var stream = pdbEntry.Open()) { stream.CopyTo(pdbBytes); }
    pdbBytes.Position = 0;
    using var provider = MetadataReaderProvider.FromPortablePdbStream(pdbBytes);
    var reader = provider.GetMetadataReader();
    var sourceLink = reader.CustomDebugInformation.Select(h => reader.GetCustomDebugInformation(h))
        .Single(c => reader.GetGuid(c.Kind) == new Guid("CC110556-A091-4D38-9FEC-25AB9A351A6A"));
    using var mappingsJson = JsonDocument.Parse(reader.GetBlobBytes(sourceLink.Value));
    var mappings = mappingsJson.RootElement.GetProperty("documents").EnumerateObject().ToArray();
    Require(mappings.Any(m => m.Value.GetString()!.StartsWith(sourcePrefix, StringComparison.Ordinal)), $"{id}: Source Link does not point at expected commit.");
    int packageDocuments = 0;
    foreach (var handle in reader.Documents) {
        var document = reader.GetDocument(handle);
        string name = reader.GetString(document.Name).Replace('\\', '/');
        foreach (var mapping in mappings) {
            string pattern = mapping.Name.Replace('\\', '/');
            if (!pattern.EndsWith('*') || !name.StartsWith(pattern[..^1], StringComparison.Ordinal)) { continue; }
            string url = mapping.Value.GetString()!.Replace("*", name[(pattern.Length - 1)..], StringComparison.Ordinal);
            if (!url.StartsWith(sourcePrefix, StringComparison.Ordinal)) { continue; }
            string relative = Uri.UnescapeDataString(url[sourcePrefix.Length..]);
            // Generated SDK documents live under obj and are embedded; tracked source checks cover src/*.cs.
            if (!relative.StartsWith("src/", StringComparison.Ordinal) || relative.Contains("/obj/", StringComparison.Ordinal)) { continue; }
            string local = Path.GetFullPath(Path.Combine(sourceRoot, relative));
            Require(local.StartsWith(sourceRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Source Link path escapes source root.");
            byte[] bytes = File.ReadAllBytes(local);
            Guid algorithm = reader.GetGuid(document.HashAlgorithm);
            byte[] hash = algorithm == new Guid("8829D00F-11B8-4213-878B-770E8597AC16") ? SHA256.HashData(bytes) :
                algorithm == new Guid("FF1816EC-AA5E-4D10-87F7-6F4963833460") ? SHA1.HashData(bytes) : throw new InvalidDataException("Unknown PDB document checksum algorithm.");
            Require(hash.AsSpan().SequenceEqual(reader.GetBlobBytes(document.Hash)), $"{id}: source checksum mismatch: {relative}");
            packageDocuments++;
            break;
        }
    }
    Require(packageDocuments > 0, $"{id}: no tracked source documents checked.");
    checkedSourceDocuments += packageDocuments;
    Console.WriteLine($"Verified {id}/{version}: package assets, provenance, portable PDB and {packageDocuments} local source checksums.");
}
using var assetsJson = JsonDocument.Parse(File.ReadAllText(args[2]));
var libraries = assetsJson.RootElement.GetProperty("libraries").EnumerateObject().ToArray();
Require(libraries.All(p => p.Value.GetProperty("type").GetString() == "package"), "Consumer assets contain a project reference.");
var actual = libraries.Where(p => p.Name.StartsWith("Atelia.", StringComparison.OrdinalIgnoreCase)).Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
Require(actual.SetEquals(expected.Select(id => id + "/" + version)), "Actual restored storage package versions differ from manifest.");
Console.WriteLine($"Package metadata and isolated dependency graph passed; {checkedSourceDocuments} local source documents checked. Remote Source Link download is not tested.");

ZipArchive OpenVerifiedArchive(JsonElement package, string fileKey, string hashKey, string extension) {
    string filename = package.GetProperty(fileKey).GetString()!;
    Require(filename == package.GetProperty("id").GetString() + "." + version + "." + extension, "Unexpected package filename.");
    string path = Path.Combine(feed, filename);
    using (var stream = File.OpenRead(path)) {
        string hash = Convert.ToHexString(SHA256.HashData(stream));
        Require(hash.Equals(package.GetProperty(hashKey).GetString(), StringComparison.OrdinalIgnoreCase), $"Hash mismatch: {filename}");
    }
    return ZipFile.OpenRead(path);
}
static void Require(bool condition, string message) {
    if (!condition) { throw new InvalidDataException(message); }
}

using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;

if (args.Length != 5) { throw new ArgumentException("Expected manifest, source root, Rbf assets, Binary assets, expected version."); }
string manifestPath = Path.GetFullPath(args[0]);
string feed = Path.GetDirectoryName(manifestPath)!;
string sourceRoot = Path.GetFullPath(args[1]);
using var manifestJson = JsonDocument.Parse(File.ReadAllText(manifestPath));
var manifest = manifestJson.RootElement;
string revision = manifest.GetProperty("sourceRevision").GetString()!;
string version = manifest.GetProperty("version").GetString()!;
Require(version == args[4], "Manifest version differs from requested version.");
int schemaVersion = manifest.GetProperty("schemaVersion").GetInt32();
Require(schemaVersion == 1, "Main requires the four-package schema 1 manifest; historical selective delivery belongs to the RBF1 branch.");
string repository = manifest.GetProperty("repositoryUrl").GetString()!;
string sourcePrefix = repository.Replace("https://github.com/", "https://raw.githubusercontent.com/", StringComparison.Ordinal) + "/" + revision + "/";
var expected = manifest.GetProperty("packages").EnumerateArray().Select(p => p.GetProperty("id").GetString()!).ToHashSet(StringComparer.Ordinal);
string[] storageIds = ["Atelia.Primitives", "Atelia.Data", "Atelia.Rbf", "Atelia.Binary"];
Require(manifest.GetProperty("packages").GetArrayLength() == storageIds.Length && expected.SetEquals(storageIds),
    "Unexpected candidate package set.");
var expectedVersions = storageIds.ToDictionary(id => id, _ => version, StringComparer.OrdinalIgnoreCase);
int checkedSourceDocuments = 0;
foreach (var package in manifest.GetProperty("packages").EnumerateArray()) {
    string id = package.GetProperty("id").GetString()!;
    using var archive = OpenVerifiedArchive(package, "file", "sha256", "nupkg", version);
    Require(archive.GetEntry(".signature.p7s") is null, $"{id}: candidate package is already signed.");
    using var symbols = OpenVerifiedArchive(package, "symbolsFile", "symbolsSha256", "snupkg", version);
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
        Require(assemblyMetadata.GetString(definition.Name) == id && definition.Version == new Version(1, 0, 0, 0), $"{id}: unexpected assembly identity/version.");
    }
    var internalDependencies = metadata.Descendants().Where(e => e.Name.LocalName == "dependency")
        .Where(e => storageIds.Contains((string)e.Attribute("id")!, StringComparer.OrdinalIgnoreCase)).ToArray();
    var externalDependencies = metadata.Descendants().Where(e => e.Name.LocalName == "dependency")
        .Where(e => !((string)e.Attribute("id")!).StartsWith("Atelia.", StringComparison.OrdinalIgnoreCase)).ToArray();
    Require(id == "Atelia.Binary"
        ? externalDependencies.Length == 1 && (string?)externalDependencies[0].Attribute("id") == "K4os.Compression.LZ4"
            && (string?)externalDependencies[0].Attribute("version") == "[1.3.8]"
        : externalDependencies.Length == 0, $"{id}: unexpected external dependency graph.");
    string[] expectedDependencies = id == "Atelia.Rbf" ? ["Atelia.Primitives", "Atelia.Data"] : [];
    Require(internalDependencies.Length == expectedDependencies.Length &&
        internalDependencies.Select(e => (string)e.Attribute("id")!).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(expectedDependencies),
        $"{id}: unexpected storage dependency graph.");
    Require(!metadata.Descendants().Where(e => e.Name.LocalName == "dependency")
        .Any(e => ((string)e.Attribute("id")!).StartsWith("Atelia.", StringComparison.OrdinalIgnoreCase) &&
            !storageIds.Contains((string)e.Attribute("id")!, StringComparer.OrdinalIgnoreCase)),
        $"{id}: dependency on a package outside the main core set.");
    foreach (var dependency in internalDependencies) {
        string dependencyId = (string)dependency.Attribute("id")!;
        string dependencyVersion = (string)dependency.Attribute("version")!;
        string requiredVersion = expectedVersions[dependencyId];
        Require(dependencyVersion == requiredVersion || dependencyVersion == $"[{requiredVersion}, )" || dependencyVersion == $"[{requiredVersion},)" || dependencyVersion == $"[{requiredVersion}]", $"{id}: unexpected storage dependency {dependencyId} {dependencyVersion}.");
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
CheckConsumer(args[2], "Atelia.Rbf", ["Atelia.Primitives", "Atelia.Data", "Atelia.Rbf"]);
CheckConsumer(args[3], "Atelia.Binary", ["Atelia.Binary"]);
Console.WriteLine($"Package metadata and both isolated dependency graphs passed; {checkedSourceDocuments} local source documents checked. Remote Source Link download is not tested.");

void CheckConsumer(string assetsPath, string directId, string[] closureIds) {
    using var assetsJson = JsonDocument.Parse(File.ReadAllText(assetsPath));
    var assets = assetsJson.RootElement;
    var libraries = assets.GetProperty("libraries").EnumerateObject().ToArray();
    Require(libraries.All(p => p.Value.GetProperty("type").GetString() == "package"), $"{directId}: consumer assets contain a project reference.");
    var closure = closureIds.Select(id => id + "/" + expectedVersions[id]).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var fullClosure = new HashSet<string>(closure, StringComparer.OrdinalIgnoreCase);
    if (directId == "Atelia.Binary") { fullClosure.Add("K4os.Compression.LZ4/1.3.8"); }
    Require(libraries.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(fullClosure),
        $"{directId}: restored full dependency graph differs from the approved closure.");
    var target = assets.GetProperty("targets").GetProperty("net10.0");
    Require(target.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(fullClosure),
        $"{directId}: full target dependency graph differs from the approved closure.");
    var packageFolders = assetsJson.RootElement.GetProperty("packageFolders").EnumerateObject().Select(p => p.Name).ToArray();
    Require(packageFolders.Length == 1, $"{directId}: expected one isolated NuGet package folder.");
    foreach (var package in manifest.GetProperty("packages").EnumerateArray().Where(p => closureIds.Contains(p.GetProperty("id").GetString()!, StringComparer.Ordinal))) {
        string id = package.GetProperty("id").GetString()!;
        string packageVersion = expectedVersions[id];
        string filename = package.GetProperty("file").GetString()!;
        string cachePath = Path.Combine(packageFolders[0], id.ToLowerInvariant(), packageVersion.ToLowerInvariant(), filename.ToLowerInvariant());
        Require(File.Exists(cachePath), $"{id}: restored package archive is missing from private cache.");
        using var cacheStream = File.OpenRead(cachePath);
        Require(Convert.ToHexString(SHA256.HashData(cacheStream)).Equals(package.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase), $"{id}: restored package bytes differ from frozen feed.");
    }
    var frameworks = assetsJson.RootElement.GetProperty("project").GetProperty("frameworks").EnumerateObject().ToArray();
    Require(frameworks.Length == 1 && frameworks[0].Name == "net10.0", $"{directId}: expected a single net10.0 smoke target framework.");
    var direct = frameworks[0].Value.GetProperty("dependencies").EnumerateObject()
        .Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    Require(direct.SetEquals([directId]), $"{directId}: smoke must directly reference only {directId}.");
    Console.WriteLine($"Verified {directId} public PackageReference: {fullClosure.Count}-package isolated closure ({closureIds.Length} Atelia) and frozen Atelia cache bytes.");
}

ZipArchive OpenVerifiedArchive(JsonElement package, string fileKey, string hashKey, string extension, string packageVersion) {
    string filename = package.GetProperty(fileKey).GetString()!;
    Require(filename == package.GetProperty("id").GetString() + "." + packageVersion + "." + extension, "Unexpected package filename.");
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

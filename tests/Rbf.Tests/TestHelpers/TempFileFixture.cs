using System;
using System.IO;

namespace Atelia.Rbf.Tests.TestHelpers;

public sealed class TempFileFixture : IDisposable {
    public TempFileFixture(string? prefix = null) {
        prefix ??= "RbfTests";
        TempDir = Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(TempDir);
    }

    public string TempDir { get; }

    public string GetFilePath(string fileName) {
        return Path.Combine(TempDir, fileName);
    }

    public void Dispose() {
        try {
            Directory.Delete(TempDir, recursive: true);
        }
        catch {
            // best-effort cleanup
        }
    }
}

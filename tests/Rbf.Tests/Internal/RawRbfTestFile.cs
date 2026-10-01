using Microsoft.Win32.SafeHandles;

namespace Atelia.Rbf.Internal.Tests;

/// <summary>Algorithm fixtures deliberately bypass public open validation to exercise corrupted images.</summary>
internal static class RawRbfTestFile {
    internal static IRbfFile OpenExisting(string path, long? tailOffset = null, RbfCacheMode cacheMode = RbfCacheMode.Off) {
        SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        try {
            return new RbfFileImpl(handle, tailOffset ?? RandomAccess.GetLength(handle), cacheMode);
        }
        catch {
            handle.Dispose();
            throw;
        }
    }

    internal static void SetLength(string path, long length) {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        RandomAccess.SetLength(handle, length);
    }
}

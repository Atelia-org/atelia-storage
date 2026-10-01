using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Atelia.Rbf.Internal.Tests;

/// <summary>为 MaxOffset 边界测试准备文件，避免 Windows 实际分配约 1 TiB 的空洞。</summary>
internal static class SparseRbfTestFile {
    private const uint FsctlSetSparse = 0x000900C4;

    internal static IRbfFile CreateNew(string path, long tailOffset = 4) {
        if (!OperatingSystem.IsWindows()) {
            using (RbfFile.CreateNew(path)) { }
            RawRbfTestFile.SetLength(path, tailOffset);
            return RawRbfTestFile.OpenExisting(path, tailOffset);
        }

        // 仍由公开 runtime API 创建合法 HeaderFence；其 FileShare.None 句柄关闭后再标记稀疏。
        using (RbfFile.CreateNew(path)) { }
        using (SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
            // FSCTL_SET_SPARSE 的 null input 等价于 SetSparse=true。
            if (!DeviceIoControl(handle, FsctlSetSparse, 0, 0, 0, 0, out _, 0)) {
                var error = new Win32Exception(Marshal.GetLastPInvokeError());
                throw new IOException(
                    $"Cannot create sparse boundary-test file '{path}'. Use a Windows TEMP directory on a filesystem that supports sparse files.",
                    error
                );
            }
            RandomAccess.SetLength(handle, tailOffset);
        }

        // Synthetic holes are not a valid main sequence; bypass public open only for offset boundary fixtures.
        return RawRbfTestFile.OpenExisting(path, tailOffset);
    }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle handle,
        uint controlCode,
        nint inputBuffer,
        uint inputBufferSize,
        nint outputBuffer,
        uint outputBufferSize,
        out uint bytesReturned,
        nint overlapped
    );
}

using System.Runtime.CompilerServices;
using Microsoft.Win32.SafeHandles;

namespace Atelia.Rbf.Internal;

internal readonly record struct RbfWriteRequest(string? Path, long Offset, int RequestedBytes);
internal readonly record struct RbfWriteObservation(string? Path, long Offset, int RequestedBytes, int WrittenBytes);
internal readonly record struct RbfFlushRequest(string? Path);
internal readonly record struct RbfSetLengthRequest(string? Path, long Length);

/// <summary>Internal experiment hooks. A partial write always fails unless its callback never returns.</summary>
internal sealed class RbfWriteHooks {
    internal Func<RbfWriteRequest, int>? BeforeWrite { get; init; }
    internal Action<RbfWriteObservation>? AfterWrite { get; init; }
    internal Action<RbfFlushRequest>? BeforeFlush { get; init; }
    internal Action<RbfFlushRequest>? AfterFlush { get; init; }
    internal Action<RbfSetLengthRequest>? BeforeSetLength { get; init; }
    internal Action<RbfSetLengthRequest>? AfterSetLength { get; init; }
}

/// <summary>Async-context instrumentation of real sequential writes and durability barriers.</summary>
internal static class RbfWriteInstrumentation {
    private sealed record FileIdentity(string Path);
    private static readonly ConditionalWeakTable<SafeFileHandle, FileIdentity> Paths = new();
    private static readonly AsyncLocal<RbfWriteHooks?> Ambient = new();

    internal static RbfWriteHooks? Current {
        get => Ambient.Value;
        set => Ambient.Value = value;
    }

    // One weak association per live factory handle; neither the table nor identity retains the handle.
    internal static void RegisterPath(SafeFileHandle handle, string path) =>
        Paths.Add(handle, new FileIdentity(System.IO.Path.GetFullPath(path)));

    private static string? GetPath(SafeFileHandle handle) =>
        Paths.TryGetValue(handle, out var identity) ? identity.Path : null;

    internal static void Write(SafeFileHandle handle, ReadOnlySpan<byte> data, long offset) {
        var hooks = Current;
        if (hooks is null) {
            RandomAccess.Write(handle, data, offset);
            return;
        }
        string? path = GetPath(handle);
        int count = hooks.BeforeWrite?.Invoke(new(path, offset, data.Length)) ?? data.Length;
        if ((uint)count > (uint)data.Length) {
            throw new ArgumentOutOfRangeException(nameof(count), "Injected write length must be within the requested span.");
        }
        RandomAccess.Write(handle, data[..count], offset);
        hooks.AfterWrite?.Invoke(new(path, offset, data.Length, count));
        if (count != data.Length) {
            throw new IOException($"Injected partial RBF write at offset {offset}: {count} of {data.Length} bytes.");
        }
    }

    internal static void Flush(SafeFileHandle handle) {
        var hooks = Current;
        if (hooks is null) {
            RandomAccess.FlushToDisk(handle);
            return;
        }
        var request = new RbfFlushRequest(GetPath(handle));
        hooks.BeforeFlush?.Invoke(request);
        RandomAccess.FlushToDisk(handle);
        hooks.AfterFlush?.Invoke(request);
    }

    internal static void SetLength(SafeFileHandle handle, long length) {
        var hooks = Current;
        var request = new RbfSetLengthRequest(GetPath(handle), length);
        hooks?.BeforeSetLength?.Invoke(request);
        RandomAccess.SetLength(handle, length);
        hooks?.AfterSetLength?.Invoke(request);
    }
}

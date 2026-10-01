using Atelia.Rbf.Internal;

namespace Atelia.Rbf;

/// <summary>Thread-local, synchronous RBF read measurement for bounded experiments.</summary>
/// <remarks>
/// Counts RBF reader operations and direct factory/recovery reads (membership and streaming CRC). FileStream metadata
/// reads (journal format, catalog snapshot and segment locator) are outside this scope.
/// Raw returned bytes are bytes returned by RandomAccess.Read, not device physical I/O.
/// ReadAheadReturnedBytes is overfetch: returned raw bytes outside the current logical
/// requested range. It does not imply future use or an additional asynchronous read.
/// Nested scopes measure independently and must be disposed in reverse order on their
/// creating thread. No scope flows to another thread or across asynchronous continuations.
/// </remarks>
internal sealed class RbfReadMetrics : IDisposable {
    [ThreadStatic]
    private static RbfReadMetrics? _current;
    private readonly RbfReadMetrics? _previous;
    private readonly int _threadId;
    private RbfReadMetricsSnapshot _snapshot;
    private bool _disposed;

    private RbfReadMetrics() {
        _previous = _current;
        _threadId = Environment.CurrentManagedThreadId;
        _current = this;
    }

    internal static RbfReadMetrics? Current => _current;
    internal static RbfReadMetrics Begin() => new();
    internal RbfReadMetricsSnapshot Snapshot() => _snapshot;

    internal void Reset() {
        RequireActive();
        _snapshot = default;
    }

    internal void ReadRequested(int requested) => _snapshot = _snapshot with {
        ReadCalls = _snapshot.ReadCalls + 1,
        RequestedBytes = _snapshot.RequestedBytes + requested
    };

    internal void ReadReturned(int returned) => _snapshot = _snapshot with {
        ReturnedBytes = _snapshot.ReturnedBytes + returned
    };

    internal void RawRequested(int requested) => _snapshot = _snapshot with {
        RawReadCalls = _snapshot.RawReadCalls + 1,
        RawRequestedBytes = _snapshot.RawRequestedBytes + requested
    };

    internal void RawReturned(long rawOffset, int returned, long logicalOffset, int logicalRequested) {
        long overlapStart = Math.Max(rawOffset, logicalOffset);
        long overlapEnd = Math.Min(rawOffset + returned, logicalOffset + logicalRequested);
        long overlap = Math.Max(0, overlapEnd - overlapStart);
        _snapshot = _snapshot with {
            RawReturnedBytes = _snapshot.RawReturnedBytes + returned,
            ReadAheadReturnedBytes = _snapshot.ReadAheadReturnedBytes + returned - overlap
        };
    }

    internal void HeaderRequested(int requested) {
        _snapshot = _snapshot with { HeaderReadCalls = _snapshot.HeaderReadCalls + 1 };
        ReadRequested(requested);
        RawRequested(requested);
    }

    internal void HeaderReturned(int returned) {
        ReadReturned(returned);
        RawReturned(0, returned, 0, RbfLayout.FenceSize);
    }

    public void Dispose() {
        if (_disposed) { return; }
        RequireActive();
        _current = _previous;
        _disposed = true;
    }

    private void RequireActive() {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Environment.CurrentManagedThreadId != _threadId || _current != this) {
            throw new InvalidOperationException("RBF read metrics scopes require their creating thread and reverse disposal order.");
        }
    }
}

internal readonly record struct RbfReadMetricsSnapshot(
    long ReadCalls,
    long RequestedBytes,
    long ReturnedBytes,
    long RawReadCalls,
    long RawRequestedBytes,
    long RawReturnedBytes,
    long ReadAheadReturnedBytes,
    long HeaderReadCalls
);

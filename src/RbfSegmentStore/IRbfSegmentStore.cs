using Atelia.Rbf;

namespace Atelia.RbfSegmentStore;

public interface IRbfSegmentStore : IDisposable {
    RbfSegmentWriterLease OpenActiveWriter();
    RbfSegmentReaderLease OpenReader(uint segmentNumber);

    uint ActiveSegmentNumber { get; }
    RbfSegmentStoreOptions Options { get; }
}

internal enum RbfSegmentLeaseKind {
    Active,
    Historical
}

internal sealed class RbfSegmentLeaseState {
    private readonly RbfSegmentStore _owner;
    private readonly RbfSegmentLeaseKind _kind;
    private bool _disposed;

    internal RbfSegmentLeaseState(RbfSegmentStore owner, uint segmentNumber, IRbfFile file, RbfSegmentLeaseKind kind) {
        _owner = owner;
        SegmentNumber = segmentNumber;
        File = file;
        _kind = kind;
    }

    internal uint SegmentNumber { get; }
    internal IRbfFile File { get; }

    internal void ThrowIfDisposed() {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    internal void Dispose() {
        if (_disposed) { return; }
        _disposed = true;
        _owner.ReleaseLease(SegmentNumber, _kind);
    }
}

public readonly struct RbfSegmentWriterLease : IDisposable {
    private readonly RbfSegmentLeaseState? _state;

    internal RbfSegmentWriterLease(RbfSegmentLeaseState state) {
        _state = state;
    }

    public uint SegmentNumber {
        get {
            var state = GetState();
            return state.SegmentNumber;
        }
    }

    public IRbfFile File {
        get {
            var state = GetState();
            return state.File;
        }
    }

    private RbfSegmentLeaseState GetState() {
        var state = _state ?? throw new ObjectDisposedException(nameof(RbfSegmentWriterLease));
        state.ThrowIfDisposed();
        return state;
    }

    public void Dispose() {
        _state?.Dispose();
    }
}

public readonly struct RbfSegmentReaderLease : IDisposable {
    private readonly RbfSegmentLeaseState? _state;

    internal RbfSegmentReaderLease(RbfSegmentLeaseState state) {
        _state = state;
    }

    public uint SegmentNumber {
        get {
            var state = GetState();
            return state.SegmentNumber;
        }
    }

    public IRbfFile File {
        get {
            var state = GetState();
            return state.File;
        }
    }

    private RbfSegmentLeaseState GetState() {
        var state = _state ?? throw new ObjectDisposedException(nameof(RbfSegmentReaderLease));
        state.ThrowIfDisposed();
        return state;
    }

    public void Dispose() {
        _state?.Dispose();
    }
}

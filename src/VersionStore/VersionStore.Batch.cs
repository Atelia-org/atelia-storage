using Atelia.Data;
using Atelia.Rbf;
using Atelia.VersionStore.Internal.Format;
using Atelia.VersionStore.Internal.Records;
using Atelia.VersionStore.Internal.Runtime;

namespace Atelia.VersionStore;

public sealed partial class VersionStore {
    /// <summary>以一个共同发布判据整体替换多个既有 ref 的 root payload；结果自有且只读。</summary>
    /// <remarks>同步捕获有效输入 slice；所有成员共享 outcome，失败不返回部分结果。</remarks>
    public AteliaResult<IReadOnlyDictionary<RefId, RefRevision>> PublishRefs(
        IReadOnlyDictionary<RefId, ReadOnlyMemory<byte>> changes, out PublicationOutcome outcome) {
        outcome = PublicationOutcome.NotAttempted;
        AteliaError? rejection = GetMutationRejection();
        if (rejection is not null) { return rejection; }
        if (IsReadOnly) { return new VersionStoreRuntimeError("ReadOnly", "This VersionStore owner is read-only."); }
        ArgumentNullException.ThrowIfNull(changes);

        // Caller-controlled enumeration, memory access and enumerator disposal all precede ref I/O.
        // Count, Keys, indexers and the caller's comparer are never used.
        List<CapturedRefChange> captured = [];
        HashSet<uint> seen = [];
        foreach (var pair in changes) {
            if (!pair.Key.BelongsTo(_identity)) { return InvalidRef(); }
            if (!seen.Add(pair.Key.LocalId)) {
                return new VersionStoreRuntimeError("ArgumentError", "A batch must contain distinct actual RefIds.");
            }
            if (pair.Value.Length is < 1 or > MaxRootPayloadLength) {
                return new VersionStoreRuntimeError("ArgumentError", "Root payload length must be between 1 and 1,048,576 bytes.");
            }
            captured.Add(new CapturedRefChange(pair.Key, pair.Value.Span.ToArray()));
        }
        EnsureOwnerUsable();
        if (captured.Count == 0) {
            return new VersionStoreRuntimeError("ArgumentError", "A publication batch must not be empty.");
        }
        if (captured.Count == 1) {
            CapturedRefChange only = captured[0];
            var published = PublishCapturedRef(only.RefId, only.Payload, ref outcome);
            if (published.IsFailure) { return published.Error!; }
            try {
                return AteliaResult<IReadOnlyDictionary<RefId, RefRevision>>.Success(
                    new PublishedRefDictionary(only.RefId, published.Value));
            }
            catch (Exception error) {
                FaultAndCleanup(error);
                throw;
            }
        }

        captured.Sort(static (left, right) => left.RefId.LocalId.CompareTo(right.RefId.LocalId));
        long tableLength = ConditionalUpdateCodec.CountSize + (long)ConditionalUpdateCodec.MemberSize * captured.Count;
        // Complete capacity checks before opening any target. No arbitrary member-count ceiling is added.
        foreach (CapturedRefChange change in captured) {
            long payloadLength = tableLength + change.Payload.Length;
            if (payloadLength > ConditionalUpdateCodec.MaxPayloadLength) {
                return new VersionStoreRuntimeError("RefCapacityExceeded", "The CU envelope and root payload exceed the public RBF capacity.");
            }
            change.PayloadLength = (int)payloadLength;
            var measured = RbfFile.MeasureWriteSize(change.PayloadLength, RecordMetadataCodec.EncodedSize);
            if (measured.IsFailure) { return measured.Error!; }
            change.FrameLength = measured.Value.FrameLength;
        }
        var members = new ConditionalUpdateMember[captured.Count];
        for (int index = 0; index < captured.Count; index++) {
            CapturedRefChange change = captured[index];
            var opened = GetOrOpen(change.RefId.LocalId);
            if (opened.IsFailure) { return opened.Error!; }
            IRbfFile file = opened.Value!;
            var checkedHeader = RefRecordReader.CheckHeader(file, _identity, change.RefId);
            if (checkedHeader.IsFailure) { return checkedHeader.Error!; }
            var checkedCurrent = CheckLocalCurrent(file, checkedHeader.Value);
            if (checkedCurrent.IsFailure) { return checkedCurrent.Error!; }
            if (!SizedPtr.TryCreate(file.TailOffset, change.FrameLength, out SizedPtr predicted)) {
                return new VersionStoreRuntimeError("RefCapacityExceeded", "The real ref tail cannot represent another complete CU ticket.");
            }
            change.File = file;
            change.Ticket = predicted;
            members[index] = new ConditionalUpdateMember(change.RefId.LocalId, predicted);
        }
        byte[] encodedMembers = ConditionalUpdateCodec.EncodeMembers(members);
        long recordedAt = _testHooks?.GetRecordedAtUnixMs?.Invoke()
            ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Span<byte> tailMeta = stackalloc byte[RecordMetadataCodec.EncodedSize];
        RecordMetadataCodec.TryWrite(recordedAt, tailMeta);

        bool completedAny = false;
        try {
            if (_testHooks?.ConfirmDataDurable is { } confirm) { confirm(); }
            else { _data.ConfirmDurable(); }
            foreach (CapturedRefChange change in captured) {
                var appended = AppendConditionalMember(change, encodedMembers, tailMeta, completedAny, ref outcome);
                if (appended.IsFailure) {
                    if (!completedAny) { return appended.Error!; }
                    // Earlier complete members remain on disk. Stop this owner without downgrading evidence.
                    appended.Unwrap();
                }
                completedAny = true;
            }
            foreach (CapturedRefChange change in captured) {
                _testHooks?.BeforeConditionalUpdateFlush?.Invoke(change.RefId.LocalId);
                change.File!.DurableFlush();
            }
            outcome = PublicationOutcome.Confirmed;
            _testHooks?.AfterPublicationConfirmed?.Invoke();
            return AteliaResult<IReadOnlyDictionary<RefId, RefRevision>>.Success(new PublishedRefDictionary(captured));
        }
        catch (Exception error) {
            FaultAndCleanup(error);
            throw;
        }
    }

    private AteliaResult<SizedPtr> AppendConditionalMember(CapturedRefChange change,
        ReadOnlySpan<byte> encodedMembers, ReadOnlySpan<byte> tailMeta, bool completedAny,
        scoped ref PublicationOutcome outcome) {
        RbfFrameBuilder builder = default;
        bool began = false;
        AteliaResult<SizedPtr> appended = default;
        CleanupErrors errors = default;
        try {
            _testHooks?.BeforeConditionalUpdateBegin?.Invoke(change.RefId.LocalId);
            builder = change.File!.BeginAppend(change.PayloadLength,
                RecordMetadataCodec.EncodedSize, out SizedPtr actualTicket);
            began = true;
            if (actualTicket != change.Ticket) {
                throw new InvalidOperationException("The actual CU ticket differs from its prepared prediction.");
            }
            RbfPayloadWriter writer = builder.PayloadAndMeta;
            WriteCapturedBytes(writer, encodedMembers);
            WriteCapturedBytes(writer, change.Payload);
            WriteCapturedBytes(writer, tailMeta);
            _testHooks?.BeforeConditionalUpdateEnd?.Invoke(change.RefId.LocalId);
            outcome = PublicationOutcome.Unknown;
            appended = builder.EndAppend(RefRecordCodec.ConditionalUpdateTag);
            if (appended.IsFailure) {
                // A public End failure proves pre-I/O rejection only for this member, never for earlier output.
                if (!completedAny) { outcome = PublicationOutcome.NotAttempted; }
            }
            else {
                if (appended.Value != actualTicket) {
                    throw new InvalidOperationException("The completed CU ticket differs from its Begin ticket.");
                }
                _testHooks?.AfterRefRecordAppended?.Invoke(appended.Value);
            }
        }
        catch (Exception error) { errors.Add(error); }
        finally {
            if (began) {
                try { builder.Dispose(); }
                catch (Exception error) { errors.Add(error); }
            }
        }
        errors.ThrowIfAny();
        return appended;
    }

    private static void WriteCapturedBytes(RbfPayloadWriter writer, ReadOnlySpan<byte> bytes) {
        bytes.CopyTo(writer.GetSpan(bytes.Length));
        writer.Advance(bytes.Length);
    }

    private sealed class CapturedRefChange(RefId refId, byte[] payload) {
        internal readonly RefId RefId = refId;
        internal readonly byte[] Payload = payload;
        internal int PayloadLength;
        internal int FrameLength;
        internal IRbfFile? File;
        internal SizedPtr Ticket;
    }

    // Only read interfaces escape; Keys/Values are iterators, so ICollection.SyncRoot cannot reveal backing.
    private sealed class PublishedRefDictionary : IReadOnlyDictionary<RefId, RefRevision> {
        private readonly Dictionary<RefId, RefRevision> _entries;

        internal PublishedRefDictionary(RefId refId, RefRevision revision) {
            _entries = new Dictionary<RefId, RefRevision>(1) { [refId] = revision };
        }

        internal PublishedRefDictionary(List<CapturedRefChange> captured) {
            _entries = new Dictionary<RefId, RefRevision>(captured.Count);
            foreach (CapturedRefChange change in captured) {
                _entries.Add(change.RefId, RefRevision.Create(change.RefId, change.Ticket));
            }
        }

        public int Count => _entries.Count;
        public RefRevision this[RefId key] => _entries[key];
        public IEnumerable<RefId> Keys {
            get { foreach (var pair in _entries) { yield return pair.Key; } }
        }
        public IEnumerable<RefRevision> Values {
            get { foreach (var pair in _entries) { yield return pair.Value; } }
        }
        public bool ContainsKey(RefId key) => _entries.ContainsKey(key);
        public bool TryGetValue(RefId key, out RefRevision value) => _entries.TryGetValue(key, out value);
        public IEnumerator<KeyValuePair<RefId, RefRevision>> GetEnumerator() {
            foreach (var pair in _entries) { yield return pair; }
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

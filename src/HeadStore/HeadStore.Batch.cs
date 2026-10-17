using Atelia.Data;
using Atelia.Rbf;
using Atelia.HeadStore.Internal.Format;
using Atelia.HeadStore.Internal.Records;
using Atelia.HeadStore.Internal.Runtime;

namespace Atelia.HeadStore;

public sealed partial class HeadStore {
    /// <summary>以一个共同发布判据整体替换多个既有 head 的 HeadEntry payload；结果自有且只读。</summary>
    /// <remarks>同步捕获有效输入 slice；所有成员共享 outcome，失败不返回部分结果。</remarks>
    public AteliaResult<IReadOnlyDictionary<HeadId, HeadEntryId>> PublishHeads(
        IReadOnlyDictionary<HeadId, ReadOnlyMemory<byte>> changes, out PublicationOutcome outcome) {
        outcome = PublicationOutcome.NotAttempted;
        AteliaError? rejection = GetMutationRejection();
        if (rejection is not null) { return rejection; }
        if (IsReadOnly) { return new HeadStoreRuntimeError("ReadOnly", "This HeadStore owner is read-only."); }
        ArgumentNullException.ThrowIfNull(changes);

        // Caller-controlled enumeration, memory access and enumerator disposal all precede head I/O.
        // Count, Keys, indexers and the caller's comparer are never used.
        List<CapturedHeadChange> captured = [];
        HashSet<uint> seen = [];
        foreach (var pair in changes) {
            if (!pair.Key.TryGetLocalId(_identity, out uint localId)) { return InvalidHead(); }
            if (!seen.Add(localId)) {
                return new HeadStoreRuntimeError("ArgumentError", "A batch must contain distinct actual HeadIds.");
            }
            if (pair.Value.Length is < 1 or > MaxHeadEntryPayloadLength) {
                return new HeadStoreRuntimeError("ArgumentError", "Root payload length must be between 1 and 1,048,576 bytes.");
            }
            captured.Add(new CapturedHeadChange(pair.Key, localId, pair.Value.Span.ToArray()));
        }
        EnsureOwnerUsable();
        if (captured.Count == 0) {
            return new HeadStoreRuntimeError("ArgumentError", "A publication batch must not be empty.");
        }
        if (captured.Count == 1) {
            CapturedHeadChange only = captured[0];
            var published = PublishCapturedHead(only.HeadId, only.Payload, ref outcome);
            if (published.IsFailure) { return published.Error!; }
            try {
                return AteliaResult<IReadOnlyDictionary<HeadId, HeadEntryId>>.Success(
                    new PublishedHeadDictionary(only.HeadId, published.Value));
            }
            catch (Exception error) {
                FaultAndCleanup(error);
                throw;
            }
        }

        captured.Sort(static (left, right) => left.LocalId.CompareTo(right.LocalId));
        long tableLength = ConditionalUpdateCodec.CountSize + (long)ConditionalUpdateCodec.MemberSize * captured.Count;
        // Complete capacity checks before opening any target. No arbitrary member-count ceiling is added.
        foreach (CapturedHeadChange change in captured) {
            long payloadLength = tableLength + change.Payload.Length;
            if (payloadLength > ConditionalUpdateCodec.MaxPayloadLength) {
                return new HeadStoreRuntimeError("HeadCapacityExceeded", "The CU envelope and root payload exceed the public RBF capacity.");
            }
            change.PayloadLength = (int)payloadLength;
            var measured = RbfFile.MeasureWriteSize(change.PayloadLength, RecordMetadataCodec.EncodedSize);
            if (measured.IsFailure) { return measured.Error!; }
            change.FrameLength = measured.Value.FrameLength;
        }
        var members = new ConditionalUpdateMember[captured.Count];
        for (int index = 0; index < captured.Count; index++) {
            CapturedHeadChange change = captured[index];
            var opened = GetOrOpen(change.LocalId);
            if (opened.IsFailure) { return opened.Error!; }
            IRbfFile file = opened.Value!;
            var checkedHeader = HeadRecordReader.CheckHeader(file, _identity, change.HeadId);
            if (checkedHeader.IsFailure) { return checkedHeader.Error!; }
            var checkedCurrent = CheckLocalCurrent(file, checkedHeader.Value);
            if (checkedCurrent.IsFailure) { return checkedCurrent.Error!; }
            if (!SizedPtr.TryCreate(file.TailOffset, change.FrameLength, out SizedPtr predicted)) {
                return new HeadStoreRuntimeError("HeadCapacityExceeded", "The real head tail cannot represent another complete CU ticket.");
            }
            change.File = file;
            change.Ticket = predicted;
            members[index] = new ConditionalUpdateMember(change.LocalId, predicted);
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
            foreach (CapturedHeadChange change in captured) {
                var appended = AppendConditionalMember(change, encodedMembers, tailMeta, completedAny, ref outcome);
                if (appended.IsFailure) {
                    if (!completedAny) { return appended.Error!; }
                    // Earlier complete members remain on disk. Stop this owner without downgrading evidence.
                    appended.Unwrap();
                }
                completedAny = true;
            }
            foreach (CapturedHeadChange change in captured) {
                _testHooks?.BeforeConditionalUpdateFlush?.Invoke(change.LocalId);
                change.File!.DurableFlush();
            }
            outcome = PublicationOutcome.Confirmed;
            _testHooks?.AfterPublicationConfirmed?.Invoke();
            return AteliaResult<IReadOnlyDictionary<HeadId, HeadEntryId>>.Success(new PublishedHeadDictionary(captured));
        }
        catch (Exception error) {
            FaultAndCleanup(error);
            throw;
        }
    }

    private AteliaResult<SizedPtr> AppendConditionalMember(CapturedHeadChange change,
        ReadOnlySpan<byte> encodedMembers, ReadOnlySpan<byte> tailMeta, bool completedAny,
        scoped ref PublicationOutcome outcome) {
        RbfFrameBuilder builder = default;
        bool began = false;
        AteliaResult<SizedPtr> appended = default;
        CleanupErrors errors = default;
        try {
            _testHooks?.BeforeConditionalUpdateBegin?.Invoke(change.LocalId);
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
            _testHooks?.BeforeConditionalUpdateEnd?.Invoke(change.LocalId);
            outcome = PublicationOutcome.Unknown;
            appended = builder.EndAppend(HeadRecordCodec.ConditionalUpdateTag);
            if (appended.IsFailure) {
                // A public End failure proves pre-I/O rejection only for this member, never for earlier output.
                if (!completedAny) { outcome = PublicationOutcome.NotAttempted; }
            }
            else {
                if (appended.Value != actualTicket) {
                    throw new InvalidOperationException("The completed CU ticket differs from its Begin ticket.");
                }
                _testHooks?.AfterHeadRecordAppended?.Invoke(appended.Value);
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

    private sealed class CapturedHeadChange(HeadId headId, uint localId, byte[] payload) {
        internal readonly HeadId HeadId = headId;
        internal readonly uint LocalId = localId;
        internal readonly byte[] Payload = payload;
        internal int PayloadLength;
        internal int FrameLength;
        internal IRbfFile? File;
        internal SizedPtr Ticket;
    }

    // Only read interfaces escape; Keys/Values are iterators, so ICollection.SyncRoot cannot reveal backing.
    private sealed class PublishedHeadDictionary : IReadOnlyDictionary<HeadId, HeadEntryId> {
        private readonly Dictionary<HeadId, HeadEntryId> _entries;

        internal PublishedHeadDictionary(HeadId headId, HeadEntryId entryId) {
            _entries = new Dictionary<HeadId, HeadEntryId>(1) { [headId] = entryId };
        }

        internal PublishedHeadDictionary(List<CapturedHeadChange> captured) {
            _entries = new Dictionary<HeadId, HeadEntryId>(captured.Count);
            foreach (CapturedHeadChange change in captured) {
                _entries.Add(change.HeadId, HeadEntryId.Create(change.HeadId, change.Ticket));
            }
        }

        public int Count => _entries.Count;
        public HeadEntryId this[HeadId key] => _entries[key];
        public IEnumerable<HeadId> Keys {
            get { foreach (var pair in _entries) { yield return pair.Key; } }
        }
        public IEnumerable<HeadEntryId> Values {
            get { foreach (var pair in _entries) { yield return pair.Value; } }
        }
        public bool ContainsKey(HeadId key) => _entries.ContainsKey(key);
        public bool TryGetValue(HeadId key, out HeadEntryId value) => _entries.TryGetValue(key, out value);
        public IEnumerator<KeyValuePair<HeadId, HeadEntryId>> GetEnumerator() {
            foreach (var pair in _entries) { yield return pair; }
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

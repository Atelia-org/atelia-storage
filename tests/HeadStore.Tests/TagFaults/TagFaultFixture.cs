using System.Buffers.Binary;
using System.Globalization;
using System.Reflection;
using System.Text;
using Atelia.Data;
using Atelia.Data.Hashing;
using Atelia.Rbf;
using Atelia.HeadStore.Internal.Format;
using Atelia.HeadStore.Internal.Tags;
using Microsoft.Win32.SafeHandles;

namespace Atelia.HeadStore.Tests.TagFaults;

/// <summary>真实 RBF3 单桶文件；反射仅访问现有 RBF 诊断接缝及同一个 owned handle。</summary>
/// <remarks>不代替目录打开/恢复、owner 清理或公开 tag 公面验收。受控修改发生在显式故障注入点。</remarks>
internal sealed class TagFaultFixture : IDisposable {
    internal const uint BucketId = 0x106;
    internal const string TargetName = "tag-48";
    internal const string OtherName = "tag-65";
    private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private readonly string _parent = OperatingSystem.IsWindows() && Directory.Exists(@"W:\")
        ? @"W:\" : Path.GetTempPath();
    internal readonly string Root;
    internal readonly string PathName;
    internal readonly StoreIdentity Identity;
    internal readonly List<int> Rents = [];
    internal readonly List<TagReadRequest> ObservedReads = [];
    internal readonly List<SizedPtr> Records = [];
    internal readonly SizedPtr HeaderTicket;
    internal IRbfFile File => _file ?? throw new InvalidOperationException("The borrower is closed.");
    private IRbfFile? _file;
    private string? _readLog;
    internal Action<long, int, bool>? BeforeRead;
    internal Action<int>? BeforeRent;

    internal TagFaultFixture(params TagFaultRecord[] records) {
        Root = Path.Combine(_parent, "HeadStore-tag-fault-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        PathName = Path.Combine(Root, "bucket.rbf");
        if (!StoreIdentity.TryRead(Convert.FromHexString("0102030405060708090A0B0C0D0E0F10"), out Identity)) {
            throw new InvalidOperationException("Invalid fixture identity.");
        }
        byte[] header = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 2);
        Identity.TryWrite(header.AsSpan(4, 16));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), BucketId);
        using IRbfFile writer = RbfFile.CreateNew(PathName, RbfCacheMode.Off);
        HeaderTicket = writer.Append(0, header).Unwrap();
        foreach (TagFaultRecord record in records) {
            Records.Add(writer.Append(record.Tag, record.Payload, record.Metadata).Unwrap());
        }
        writer.DurableFlush();
    }

    internal void Open(bool writable = false) {
        if (_file is not null) { throw new InvalidOperationException("A borrower is already open."); }
        IRbfFile acquired = writable
            ? RbfFile.OpenExisting(PathName, out _, RbfCacheMode.Off)
            : RbfFile.OpenReadOnlyExisting(PathName, RbfCacheMode.Off);
        try {
            object reader = RequiredField(acquired, "_reader");
            RequiredProperty(reader, "ReadObserver").SetValue(reader, (Action<long, int, bool>)((offset, length, raw) => {
                ObservedReads.Add(new TagReadRequest(offset, length, raw));
                BeforeRead?.Invoke(offset, length, raw);
            }));
            RequiredProperty(reader, "BufferRentObserver").SetValue(reader, (Action<int>)(length => {
                Rents.Add(length);
                BeforeRent?.Invoke(length);
            }));
            _file = acquired;
            acquired = null!;
        }
        finally { acquired?.Dispose(); }
    }

    internal CheckedTagBucketHeader CheckHeader() =>
        TagRecordReader.CheckHeader(File, Identity, BucketId).Unwrap();

    internal void BeginObservation() {
        Rents.Clear();
        ObservedReads.Clear();
        _readLog = Path.Combine(Root, "reads-" + Guid.NewGuid().ToString("N") + ".csv");
        File.SetupReadLog(_readLog);
    }

    internal void Close() {
        IRbfFile? closing = _file;
        _file = null;
        closing?.Dispose();
    }

    internal TagReadRequest[] LoggedReads() {
        if (_file is not null) { throw new InvalidOperationException("Close the real reader to flush its read log."); }
        if (_readLog is null || !System.IO.File.Exists(_readLog)) { return []; }
        return System.IO.File.ReadAllLines(_readLog)
            .Where(line => !line.StartsWith('#') && !line.StartsWith("seq,", StringComparison.Ordinal))
            .Select(line => line.Split(','))
            .Select(fields => new TagReadRequest(long.Parse(fields[1], CultureInfo.InvariantCulture),
                int.Parse(fields[2], CultureInfo.InvariantCulture), Raw: false,
                int.Parse(fields[3], CultureInfo.InvariantCulture))).ToArray();
    }

    internal void FlipClosedByte(long offset) {
        if (_file is not null) { throw new InvalidOperationException("Closed-image corruption requires no borrower."); }
        using SafeFileHandle handle = System.IO.File.OpenHandle(PathName, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Span<byte> value = stackalloc byte[1];
        if (RandomAccess.Read(handle, value, offset) != 1) { throw new InvalidOperationException("Missing fixture byte."); }
        value[0] ^= 1;
        RandomAccess.Write(handle, value, offset);
    }

    // Deliberately breaks the qualified input only at a test callback. Uses the existing owned handle,
    // so no second writer bypasses the public factory's sharing policy.
    internal void TruncateOwnedFile(long length) => RandomAccess.SetLength(OwnedHandle(), length);
    internal void FlipOwnedByte(long offset) {
        SafeFileHandle handle = OwnedHandle();
        Span<byte> value = stackalloc byte[1];
        if (RandomAccess.Read(handle, value, offset) != 1) { throw new InvalidOperationException("Missing fixture byte."); }
        value[0] ^= 1;
        RandomAccess.Write(handle, value, offset);
    }

    private SafeFileHandle OwnedHandle() => (SafeFileHandle)RequiredField(File, "_handle");
    private static object RequiredField(object instance, string name) =>
        instance.GetType().GetField(name, InstanceMembers)?.GetValue(instance)
        ?? throw new InvalidOperationException($"Missing production diagnostic field {name}.");
    private static PropertyInfo RequiredProperty(object instance, string name) =>
        instance.GetType().GetProperty(name, InstanceMembers)
        ?? throw new InvalidOperationException($"Missing production diagnostic property {name}.");

    // Independent field assembly shared with the format vectors.
    internal static byte[] Metadata(string name = TargetName, long recordedAt = -17, int entryCount = 1) =>
        Tags.TagTestWire.Metadata(name, recordedAt, checked((uint)entryCount));

    internal static byte[] Payload(int length = 12, int salt = 0) => length == 12
        ? Tags.TagTestWire.Members(checked((uint)salt + 1))
        : Enumerable.Range(0, length).Select(index => (byte)(index + salt)).ToArray();

    public void Dispose() {
        try { Close(); }
        finally {
            string resolved = System.IO.Path.GetFullPath(Root);
            string expected = System.IO.Path.GetFullPath(_parent).TrimEnd(System.IO.Path.DirectorySeparatorChar)
                + System.IO.Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(expected, StringComparison.OrdinalIgnoreCase)
                || !System.IO.Path.GetFileName(resolved).StartsWith("HeadStore-tag-fault-", StringComparison.Ordinal)) {
                throw new InvalidOperationException("Fixture cleanup escaped its generated directory.");
            }
            if (Directory.Exists(resolved)) { Directory.Delete(resolved, recursive: true); }
        }
    }
}

internal sealed record TagFaultRecord(byte[] Payload, byte[] Metadata, uint Tag = 1);
internal readonly record struct TagReadRequest(long Offset, int Length, bool Raw, int Returned = -1);

using Atelia.Rbf;

namespace Atelia.FrameStore.Internal.Runtime;

/// <summary>预先存在的 active 台账项；成功输出后只更新这些字段。</summary>
internal sealed class FrameFileEntry(uint fileId) {
    internal readonly uint FileId = fileId;
    internal IRbfFile? File;
    internal long CompletedTailOffset;
    internal bool NeedsConfirmation;
    internal bool Stopped;
    internal bool ShortLease;
    internal FrameLease? CurrentLease;

    internal bool IsAvailable => File is not null && !Stopped && !ShortLease && CurrentLease is null;
}

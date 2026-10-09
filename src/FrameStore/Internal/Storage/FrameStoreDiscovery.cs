namespace Atelia.FrameStore.Internal.Storage;

/// <summary>完整正式名称发现的结果；只保留 active 台账与实际正式最大编号。</summary>
internal sealed class FrameStoreDiscovery(IReadOnlyList<uint> activeFileIds, uint maxPublishedFileId) {
    internal IReadOnlyList<uint> ActiveFileIds { get; } = activeFileIds;
    internal uint MaxPublishedFileId { get; } = maxPublishedFileId;
}

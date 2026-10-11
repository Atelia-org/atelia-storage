namespace Atelia.VersionStore;

/// <summary>自有 GC buffer 的完整 root bytes，可跨调用及 owner 生命周期保存。</summary>
public sealed class RootPayload {
    private readonly byte[] _bytes;

    // 调用方完成全部读取资格、复制实际 payload 并归还 pooled 帧后，移交独占数组。
    // ownedBytes 不得仍与输入、pool、scratch 或其他可写接口共享；此构造不重复复制。
    internal RootPayload(byte[] ownedBytes) {
        _bytes = ownedBytes;
    }

    /// <summary>完整 root-payload bytes 的长度。</summary>
    public int Length => _bytes.Length;

    /// <summary>不包含 TailMeta 的自有 root bytes，只读访问无需 Dispose。</summary>
    public ReadOnlySpan<byte> Bytes => _bytes;
}

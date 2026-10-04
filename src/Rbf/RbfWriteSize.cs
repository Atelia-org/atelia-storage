namespace Atelia.Rbf;

/// <summary>新 RBF3 写入的精确物理尺寸。</summary>
/// <remarks>
/// 由 <see cref="RbfFile.MeasureWriteSize"/> 计算，不代表文件位置预留、成功追加或耐久。
/// default 的零值不具有预留或完成资格。
/// </remarks>
public readonly struct RbfWriteSize {
    internal RbfWriteSize(int frameLength, int appendLength) {
        FrameLength = frameLength;
        AppendLength = appendLength;
    }

    /// <summary>SizedPtr 的帧长度，不含尾 Fence。</summary>
    public int FrameLength { get; }

    /// <summary>本次追加推进文件尾部的字节数，包含尾 Fence。</summary>
    public int AppendLength { get; }
}

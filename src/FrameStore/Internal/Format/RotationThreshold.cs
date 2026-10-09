using Atelia.Data;

namespace Atelia.FrameStore.Internal.Format;

/// <summary>实例固定的软轮转阈值参数；不会改变格式或对合法末帧新增容量限制。</summary>
internal static class RotationThreshold {
    internal const long DefaultBytes = 64L * 1024 * 1024 * 1024;

    internal static void Validate(long rotationThresholdBytes) {
        if (rotationThresholdBytes < FileHeaderCodec.InitializationBoundary || rotationThresholdBytes > SizedPtr.MaxOffset) {
            throw new ArgumentOutOfRangeException(nameof(rotationThresholdBytes), rotationThresholdBytes,
                $"Rotation threshold must be between {FileHeaderCodec.InitializationBoundary} and {SizedPtr.MaxOffset} bytes.");
        }
    }
}

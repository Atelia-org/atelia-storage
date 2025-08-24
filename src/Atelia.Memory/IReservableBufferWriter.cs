using System.Buffers;

namespace Atelia.Memory;
public interface IReservableBufferWriter: IBufferWriter<byte> {
    Span<byte> ReserveSpan(int count, out int reservationToken); // 简单起见，count就是准确的字节数。用于预留给上层逻辑回填数据。初始化为0。
    void Commit(int reservationToken); // 用于上层逻辑回填数据后，告知接口实现已经回填完毕，可以不再保留这块内存了。
}

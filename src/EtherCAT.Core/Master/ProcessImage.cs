namespace EtherCAT.Master;

/// <summary>
/// 逻辑过程数据镜像：所有从站的 PDO 都映射到这块内存的一个位区间
/// </summary>
public sealed class ProcessImage
{
    private byte[] _buffer = new byte[4096];

    public byte[] Buffer => _buffer;
    public int Size { get; private set; }

    /// <summary>重新分配镜像（配置阶段调用）</summary>
    public void Resize(int size)
    {
        size = Math.Max(1, size);
        if (_buffer.Length < size)
            _buffer = new byte[size + 64];
        Size = size;
        Array.Clear(_buffer, 0, _buffer.Length);
    }

    public void Clear() => Array.Clear(_buffer, 0, _buffer.Length);

    public ulong ReadBits(int bitOffset, int bitLength)
    {
        ulong value = 0;
        for (int i = 0; i < bitLength; i++)
        {
            int bit = bitOffset + i;
            if ((_buffer[bit >> 3] & (1 << (bit & 7))) != 0)
                value |= 1UL << i;
        }
        return value;
    }

    public void WriteBits(int bitOffset, int bitLength, ulong value)
    {
        for (int i = 0; i < bitLength; i++)
        {
            int bit = bitOffset + i;
            int byteIndex = bit >> 3;
            int mask = 1 << (bit & 7);
            if (((value >> i) & 1) != 0)
                _buffer[byteIndex] = (byte)(_buffer[byteIndex] | mask);
            else
                _buffer[byteIndex] = (byte)(_buffer[byteIndex] & ~mask);
        }
    }

    public byte ReadByte(int offset) => _buffer[offset];

    public void WriteByte(int offset, byte value) => _buffer[offset] = value;

    public string ToHexString(int length)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < Math.Min(length, Size); i++)
            sb.Append(_buffer[i].ToString("X2")).Append(' ');
        return sb.ToString().Trim();
    }
}

using System.Buffers.Binary;

namespace EtherCAT.Protocol;

/// <summary>
/// 单个 EtherCAT 数据报（请求内容与响应结果复用同一对象）
/// </summary>
public sealed class EthercatDatagram
{
    public EthercatCommand Command { get; internal set; }
    public byte Index { get; internal set; }
    public ushort Adp { get; internal set; }
    public ushort Ado { get; internal set; }
    /// <summary>请求负载（写操作时为待写入数据，读操作时长度即期望读取长度）</summary>
    public byte[] Data { get; internal set; } = Array.Empty<byte>();
    /// <summary>响应工作计数器</summary>
    public ushort WorkingCounter { get; internal set; }
    /// <summary>是否收到了该数据报的响应</summary>
    public bool Responded { get; internal set; }
}

/// <summary>
/// EtherCAT 以太网帧构造 / 解析工具
/// 帧结构：Ethernet II(14) + EtherCAT 头(2) + N × (数据报头(10) + 数据 + WKC(2))
/// </summary>
public sealed class EthercatFrame
{
    public const int MaxSize = Esc.MaxFrameSize;

    public byte[] Buffer { get; } = new byte[MaxSize];
    public int Length { get; private set; }
    public List<EthercatDatagram> Datagrams { get; } = new();

    private int _nextIndex = 1;

    /// <summary>开始构造一个新帧</summary>
    public void Reset(ReadOnlySpan<byte> sourceMac = default)
    {
        Datagrams.Clear();
        Length = Esc.EthernetHeaderSize + Esc.EthercatHeaderSize;
        Array.Clear(Buffer, 0, Buffer.Length);
        Buffer.AsSpan(0, 6).Fill(0xFF); // 目的 MAC：广播
        if (sourceMac.Length == 6)
            sourceMac.CopyTo(Buffer.AsSpan(6, 6));
        BinaryPrimitives.WriteUInt16BigEndian(Buffer.AsSpan(12, 2), Esc.EtherType);
        _nextIndex = 1;
    }

    /// <summary>
    /// 追加一个数据报
    /// </summary>
    public EthercatDatagram Add(EthercatCommand command, ushort adp, ushort ado, ReadOnlySpan<byte> data)
    {
        if (Datagrams.Count > 0)
        {
            // 前一个数据报标记“后续还有数据报”
            int prevLengthOffset = DatagramLengthOffset(Datagrams.Count - 1);
            ushort prev = BinaryPrimitives.ReadUInt16LittleEndian(Buffer.AsSpan(prevLengthOffset, 2));
            BinaryPrimitives.WriteUInt16LittleEndian(Buffer.AsSpan(prevLengthOffset, 2), (ushort)(prev | Esc.DatagramFollows));
        }

        var dg = new EthercatDatagram
        {
            Command = command,
            Index = (byte)(_nextIndex++ & 0xFF),
            Adp = adp,
            Ado = ado,
            Data = data.ToArray()
        };

        int pos = Length;
        Buffer[pos++] = (byte)command;
        Buffer[pos++] = dg.Index;
        BinaryPrimitives.WriteUInt16LittleEndian(Buffer.AsSpan(pos, 2), adp); pos += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(Buffer.AsSpan(pos, 2), ado); pos += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(Buffer.AsSpan(pos, 2), (ushort)dg.Data.Length); pos += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(Buffer.AsSpan(pos, 2), 0); pos += 2; // IRQ
        dg.Data.CopyTo(Buffer.AsSpan(pos, dg.Data.Length)); pos += dg.Data.Length;
        BinaryPrimitives.WriteUInt16LittleEndian(Buffer.AsSpan(pos, 2), 0); pos += 2; // WKC 占位
        Length = pos;
        Datagrams.Add(dg);
        return dg;
    }

    /// <summary>完成帧构造（写入 EtherCAT 头中的长度字段）</summary>
    public void Finish()
    {
        int ecatLength = Length - Esc.EthernetHeaderSize - Esc.EthercatHeaderSize;
        BinaryPrimitives.WriteUInt16LittleEndian(Buffer.AsSpan(Esc.EthernetHeaderSize, 2),
            (ushort)((ecatLength & 0x7FF) | (1 << 12)));
    }

    /// <summary>
    /// 解析返回的帧，把负载与 WKC 填回对应的数据报对象
    /// </summary>
    public bool TryParseResponse(byte[] response, int responseLength)
    {
        if (responseLength < Esc.EthernetHeaderSize + Esc.EthercatHeaderSize + Esc.DatagramHeaderSize + Esc.WorkCounterSize)
            return false;
        if (BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(12, 2)) != Esc.EtherType)
            return false;

        int pos = Esc.EthernetHeaderSize + Esc.EthercatHeaderSize;
        int dgIndex = 0;
        while (pos + Esc.DatagramHeaderSize + Esc.WorkCounterSize <= responseLength)
        {
            byte cmd = response[pos];
            byte idx = response[pos + 1];
            ushort adp = BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(pos + 2, 2));
            ushort ado = BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(pos + 4, 2));
            ushort lenFlag = BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(pos + 6, 2));
            int dataLength = lenFlag & 0x7FFF;
            pos += Esc.DatagramHeaderSize;

            if (pos + dataLength + Esc.WorkCounterSize > responseLength)
                return false;

            var target = dgIndex < Datagrams.Count ? Datagrams[dgIndex] : null;
            // SOEM 按 index 重组合，此处 index 相同即视为同一数据报
            if (target is { Index: var ti } && ti != idx)
            {
                target = Datagrams.FirstOrDefault(d => d.Index == idx);
            }

            if (target != null)
            {
                target.WorkingCounter = BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(pos + dataLength, 2));
                target.Responded = true;
                int copy = Math.Min(dataLength, target.Data.Length);
                if (copy > 0)
                    response.AsSpan(pos, copy).CopyTo(target.Data);
            }

            pos += dataLength + Esc.WorkCounterSize;
            dgIndex++;
            if ((lenFlag & Esc.DatagramFollows) == 0)
                break;
        }

        return true;
    }

    private int DatagramLengthOffset(int datagramIndex)
    {
        int pos = Esc.EthernetHeaderSize + Esc.EthercatHeaderSize;
        for (int i = 0; i < datagramIndex; i++)
        {
            int len = BinaryPrimitives.ReadUInt16LittleEndian(Buffer.AsSpan(pos + 6, 2)) & 0x7FFF;
            pos += Esc.DatagramHeaderSize + len + Esc.WorkCounterSize;
        }
        return pos + 6;
    }
}

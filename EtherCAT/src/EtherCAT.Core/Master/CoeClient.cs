using System.Buffers.Binary;
using EtherCAT.Protocol;

namespace EtherCAT.Master;

/// <summary>
/// CoE（CANopen over EtherCAT）客户端：邮箱收发与 SDO 快速传输
/// </summary>
public sealed class CoeClient
{
    private readonly EthercatMaster _master;
    private readonly Dictionary<int, byte> _mailboxCounters = new();

    public int TimeoutMs { get; set; } = 500;

    internal CoeClient(EthercatMaster master) => _master = master;

    private byte NextCounter(int slave)
    {
        byte counter = _mailboxCounters.TryGetValue(slave, out var c) ? c : (byte)0;
        counter = (byte)((counter + 1) & 0x07);
        _mailboxCounters[slave] = counter;
        return counter;
    }

    /// <summary>SDO 上传（读取对象字典）</summary>
    public byte[] Upload(int slave, ushort index, byte subIndex)
    {
        var slaveInfo = _master.GetSlave(slave);
        if (!slaveInfo.HasMailbox)
            throw new EthercatException($"从站 #{slave} 不支持邮箱通信，无法进行 SDO 访问");

        var request = new byte[Mailbox.HeaderSize + Coe.HeaderSize + 1 + 2 + 1 + 4];
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(0, 2), (ushort)(request.Length - Mailbox.HeaderSize));
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(2, 2), 0);
        request[4] = 0;
        request[5] = Mailbox.BuildTypeField(Mailbox.Coe, NextCounter(slave));
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(6, 2), Coe.BuildHeader(0, Coe.SdoRequest));
        request[8] = Coe.UploadInitiate;
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(9, 2), index);
        request[11] = subIndex;

        byte[]? response = Exchange(slave, request);
        if (response == null)
            throw new EthercatException($"从站 #{slave} SDO 上传 0x{index:X4}:{subIndex} 超时");

        return ParseUploadResponse(slave, index, subIndex, response);
    }

    /// <summary>SDO 下载（写入对象字典），数据长度不超过 4 字节时使用快速传输</summary>
    public bool Download(int slave, ushort index, byte subIndex, byte[] data)
    {
        var slaveInfo = _master.GetSlave(slave);
        if (!slaveInfo.HasMailbox)
            throw new EthercatException($"从站 #{slave} 不支持邮箱通信，无法进行 SDO 访问");

        if (data.Length is < 1 or > 4)
            throw new EthercatException("当前仅支持 1..4 字节的快速 SDO 下载");

        var request = new byte[Mailbox.HeaderSize + Coe.HeaderSize + 1 + 2 + 1 + 4];
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(0, 2), (ushort)(request.Length - Mailbox.HeaderSize));
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(2, 2), 0);
        request[4] = 0;
        request[5] = Mailbox.BuildTypeField(Mailbox.Coe, NextCounter(slave));
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(6, 2), Coe.BuildHeader(0, Coe.SdoRequest));
        request[8] = (byte)(Coe.DownloadExpedited | ((4 - data.Length) << 2));
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(9, 2), index);
        request[11] = subIndex;
        for (int i = 0; i < 4; i++)
            request[12 + i] = i < data.Length ? data[i] : (byte)0;

        byte[]? response = Exchange(slave, request);
        if (response == null)
            throw new EthercatException($"从站 #{slave} SDO 下载 0x{index:X4}:{subIndex} 超时");

        if (response.Length < 3)
            throw new EthercatException("SDO 响应长度不足");

        ushort header = BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(0, 2));
        if (Coe.GetService(header) != Coe.SdoResponse)
            throw new EthercatException($"SDO 响应服务类型错误 (0x{Coe.GetService(header):X})");

        byte command = response[2];
        if (command == Coe.Abort)
        {
            uint abortCode = response.Length >= 10
                ? BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(6, 4))
                : 0;
            throw new EthercatException($"SDO 下载被中止：{Coe.DescribeAbort(abortCode)}");
        }

        return command == 0x60;
    }

    private byte[] ParseUploadResponse(int slave, ushort index, byte subIndex, byte[] response)
    {
        if (response.Length < 3)
            throw new EthercatException("SDO 响应长度不足");

        ushort header = BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(0, 2));
        if (Coe.GetService(header) != Coe.SdoResponse)
            throw new EthercatException($"SDO 响应服务类型错误 (0x{Coe.GetService(header):X})");

        byte command = response[2];
        if (command == Coe.Abort)
        {
            uint abortCode = response.Length >= 10
                ? BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(6, 4))
                : 0;
            throw new EthercatException($"SDO 上传 0x{index:X4}:{subIndex} 被中止：{Coe.DescribeAbort(abortCode)}");
        }

        bool expedited = (command & 0x02) != 0;
        if (!expedited)
            throw new EthercatException("暂不支持分段 SDO 传输（报文大于 4 字节）");

        int size = (command & 0x01) != 0 ? 4 - ((command >> 2) & 0x03) : 4;
        var result = new byte[size];
        for (int i = 0; i < size && i + 6 < response.Length; i++)
            result[i] = response[6 + i];
        return result;
    }

    /// <summary>发送一个邮箱报文并等待响应</summary>
    private byte[]? Exchange(int slave, byte[] request)
    {
        var slaveInfo = _master.GetSlave(slave);

        if (!WaitMailboxFree(slaveInfo))
            return null;

        var write = new byte[Math.Max(request.Length, slaveInfo.MailboxWriteSize)];
        Array.Copy(request, write, request.Length);
        if (_master.Fpwr(slaveInfo.ConfiguredAddress, slaveInfo.MailboxWriteOffset, write) <= 0)
            return null;

        var deadline = DateTime.UtcNow.AddMilliseconds(TimeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var statusBuffer = new byte[1];
            if (_master.Fprd(slaveInfo.ConfiguredAddress, Esc.SmStatus(1), statusBuffer) <= 0)
                return null;

            if ((statusBuffer[0] & 0x08) != 0)
            {
                var mailbox = new byte[Math.Max(8, (int)slaveInfo.MailboxReadSize)];
                if (_master.Fprd(slaveInfo.ConfiguredAddress, slaveInfo.MailboxReadOffset, mailbox) <= 0)
                    return null;

                int length = BinaryPrimitives.ReadUInt16LittleEndian(mailbox.AsSpan(0, 2));
                if (length == 0 || length + Mailbox.HeaderSize > mailbox.Length)
                    return null;
                if (Mailbox.GetType(mailbox[5]) != Mailbox.Coe)
                    continue;

                var payload = new byte[length];
                Array.Copy(mailbox, Mailbox.HeaderSize, payload, 0, length);
                return payload;
            }

            Thread.Sleep(1);
        }

        return null;
    }

    private bool WaitMailboxFree(SlaveInfo slaveInfo)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(TimeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var status = new byte[1];
            if (_master.Fprd(slaveInfo.ConfiguredAddress, Esc.SmStatus(0), status) <= 0)
                return false;
            if ((status[0] & 0x08) == 0)
                return true;
            Thread.Sleep(1);
        }
        return false;
    }

    // ---- 便捷读写 ----

    public byte ReadByte(int slave, ushort index, byte subIndex) => Upload(slave, index, subIndex)[0];

    public ushort ReadUInt16(int slave, ushort index, byte subIndex) =>
        BinaryPrimitives.ReadUInt16LittleEndian(Upload(slave, index, subIndex));

    public uint ReadUInt32(int slave, ushort index, byte subIndex) =>
        BinaryPrimitives.ReadUInt32LittleEndian(Upload(slave, index, subIndex));

    public int ReadInt32(int slave, ushort index, byte subIndex) =>
        (int)ReadUInt32(slave, index, subIndex);

    public void WriteByte(int slave, ushort index, byte subIndex, byte value) =>
        Download(slave, index, subIndex, new[] { value });

    public void WriteUInt16(int slave, ushort index, byte subIndex, ushort value)
    {
        var buffer = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, value);
        Download(slave, index, subIndex, buffer);
    }

    public void WriteUInt32(int slave, ushort index, byte subIndex, uint value)
    {
        var buffer = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        Download(slave, index, subIndex, buffer);
    }

    public void WriteInt32(int slave, ushort index, byte subIndex, int value) =>
        WriteUInt32(slave, index, subIndex, unchecked((uint)value));
}

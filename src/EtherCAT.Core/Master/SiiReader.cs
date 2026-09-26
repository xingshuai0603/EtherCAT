using EtherCAT.Protocol;

namespace EtherCAT.Master;

/// <summary>从站 EEPROM 固定区身份信息</summary>
public sealed record SiiIdentity(uint VendorId, uint ProductCode, uint RevisionNumber, uint SerialNumber);

/// <summary>EEPROM 固定区邮箱配置</summary>
public sealed record SiiMailbox(ushort WriteOffset, ushort WriteSize, ushort ReadOffset, ushort ReadSize, ushort Protocol);

/// <summary>SII 中的同步管理器配置</summary>
public sealed class SiiSyncManager
{
    public int Index { get; set; }
    public ushort PhysicalStart { get; set; }
    public ushort Length { get; set; }
    public byte Control { get; set; }
    public byte Activate { get; set; }
}

/// <summary>SII 中的 PDO 概要</summary>
public sealed class SiiPdoInfo
{
    public ushort Index { get; set; }
    public int SyncManager { get; set; }
    public int BitSize { get; set; }
}

/// <summary>
/// 从站信息接口（SII/EEPROM）读取器，带字节级缓存，结构与 SOEM ec_sii* 一致
/// </summary>
public sealed class SiiReader
{
    private readonly Func<ushort, byte[]> _readWord;
    private readonly byte[] _cache = new byte[0x1000];
    private readonly bool[] _valid = new bool[0x1000];

    /// <param name="readWord">按字地址读取 EEPROM，返回该字起的若干字节（4 或 8）</param>
    public SiiReader(Func<ushort, byte[]> readWord) => _readWord = readWord;

    public byte GetByte(int byteAddress)
    {
        if (byteAddress < 0 || byteAddress >= _cache.Length)
            return 0xFF;

        if (!_valid[byteAddress])
        {
            int wordAddress = byteAddress >> 1;
            byte[] data = _readWord((ushort)wordAddress);
            for (int i = 0; i < data.Length; i++)
            {
                int index = (wordAddress << 1) + i;
                if (index >= 0 && index < _cache.Length)
                {
                    _cache[index] = data[i];
                    _valid[index] = true;
                }
            }
        }

        return _valid[byteAddress] ? _cache[byteAddress] : (byte)0xFF;
    }

    public ushort GetWord(int byteAddress) =>
        (ushort)(GetByte(byteAddress) | (GetByte(byteAddress + 1) << 8));

    public uint GetUInt32(int byteAddress) =>
        (uint)(GetWord(byteAddress) | ((uint)GetWord(byteAddress + 2) << 16));

    /// <summary>查找类别，返回长度字的字节地址（0 = 未找到）</summary>
    public int FindCategory(int category)
    {
        int address = Sii.CategoryStartByte;
        for (int guard = 0; guard < 256; guard++)
        {
            ushort type = GetWord(address);
            if (type == 0xFFFF)
                return 0;
            ushort length = GetWord(address + 2);
            if (type == category)
                return address + 2;
            if (length == 0)
                return 0;
            address += 4 + length * 2;
        }
        return 0;
    }

    /// <summary>读取固定区身份（字地址 8/0x0A/0x0C/0x0E）</summary>
    public SiiIdentity ReadIdentity() => new(
        GetUInt32(Sii.VendorIdWord * 2),
        GetUInt32(Sii.ProductCodeWord * 2),
        GetUInt32(Sii.RevisionWord * 2),
        GetUInt32(Sii.SerialWord * 2));

    /// <summary>读取固定区邮箱配置</summary>
    public SiiMailbox ReadMailbox() => new(
        GetWord(Sii.StdRxMailboxWord * 2),
        GetWord((Sii.StdRxMailboxWord + 1) * 2),
        GetWord(Sii.StdTxMailboxWord * 2),
        GetWord((Sii.StdTxMailboxWord + 1) * 2),
        GetWord(Sii.MailboxProtocolWord * 2));

    /// <summary>读取字符串类别中的第 n 个字符串</summary>
    public string ReadString(int index)
    {
        int start = FindCategory(Sii.CatStrings);
        if (start == 0) return string.Empty;

        int address = start + 2;
        int count = GetByte(address++);
        if (index < 1 || index > count)
            return string.Empty;

        for (int i = 1; i <= index; i++)
        {
            int length = GetByte(address++);
            if (i == index)
            {
                var bytes = new byte[length];
                for (int j = 0; j < length; j++)
                    bytes[j] = GetByte(address + j);
                return System.Text.Encoding.UTF8.GetString(bytes);
            }
            address += length;
        }

        return string.Empty;
    }

    /// <summary>读取同步管理器类别</summary>
    public List<SiiSyncManager> ReadSyncManagers()
    {
        var list = new List<SiiSyncManager>();
        int start = FindCategory(Sii.CatSyncManager);
        if (start == 0) return list;

        int address = start + 2;
        int count = GetWord(address);
        address += 2;
        for (int i = 0; i < count; i++)
        {
            var sm = new SiiSyncManager { Index = i };
            sm.PhysicalStart = GetWord(address); address += 2;
            sm.Length = GetWord(address); address += 2;
            sm.Control = GetByte(address++);      // control
            byte status = GetByte(address++);     // status（未使用）
            sm.Activate = GetByte(address++);     // activate
            address++;                            // pdi control
            list.Add(sm);
        }
        return list;
    }

    /// <summary>读取 PDO 类别（50 = TxPdo，51 = RxPdo）</summary>
    public List<SiiPdoInfo> ReadPdos(int category)
    {
        var list = new List<SiiPdoInfo>();
        int start = FindCategory(category);
        if (start == 0) return list;

        int address = start + 2;
        int count = GetWord(address); address += 2;
        for (int i = 0; i < count; i++)
        {
            var info = new SiiPdoInfo
            {
                Index = GetWord(address)
            };
            address += 2;
            int entryCount = GetByte(address++);
            info.SyncManager = GetByte(address++);
            address += 4;   // name index + flags
            int bitSize = 0;
            for (int e = 0; e < entryCount; e++)
            {
                address += 5;   // index(2) + subindex(1) + name index(2)
                bitSize += GetByte(address++);
                address += 2;   // data type index(2)
            }
            info.BitSize = bitSize;
            list.Add(info);
        }
        return list;
    }
}

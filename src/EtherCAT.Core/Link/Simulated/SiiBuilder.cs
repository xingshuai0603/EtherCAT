using EtherCAT.Esi;
using EtherCAT.Protocol;

namespace EtherCAT.Link.Simulated;

/// <summary>
/// 生成从站 SII（EEPROM）内容，用于仿真从站；结构遵循 ETG.1000.6 与 SOEM 的解析约定
/// </summary>
public static class SiiBuilder
{
    public sealed class Options
    {
        public uint VendorId { get; set; }
        public uint ProductCode { get; set; }
        public uint RevisionNumber { get; set; } = 0x00010000;
        public uint SerialNumber { get; set; }
        public string Name { get; set; } = "Simulated Slave";

        public ushort RxMailboxOffset { get; set; } = 0x1000;
        public ushort RxMailboxSize { get; set; } = 128;
        public ushort TxMailboxOffset { get; set; } = 0x1080;
        public ushort TxMailboxSize { get; set; } = 128;

        public List<EsiSyncManager> SyncManagers { get; } = new();
        public List<EsiPdo> RxPdos { get; } = new();
        public List<EsiPdo> TxPdos { get; } = new();

        public bool SupportsDc { get; set; }
        public ushort AssignActivate { get; set; }
        public int CycleTimeSync0 { get; set; }
        public int ShiftTimeSync0 { get; set; }
    }

    /// <summary>生成 EEPROM 字节镜像（按字节寻址，字内小端）</summary>
    public static byte[] Build(Options o)
    {
        var words = new List<ushort>(new ushort[Sii.CategoryStartWord]);

        // 固定信息区
        words[0x00] = 0x000C;                 // PDI control
        words[0x01] = 0x0003;                 // PDI configuration
        words[0x02] = 0x0064;                 // SYNC pulse length
        words[0x03] = 0x0000;                 // extended PDI configuration
        words[0x04] = 0x0000;                 // configured station alias
        words[0x05] = 0x0000;
        words[0x06] = 0x0000;
        words[0x07] = 0x0000;                 // checksum

        SetUInt32(words, Sii.VendorIdWord, o.VendorId);
        SetUInt32(words, Sii.ProductCodeWord, o.ProductCode);
        SetUInt32(words, Sii.RevisionWord, o.RevisionNumber);
        SetUInt32(words, Sii.SerialWord, o.SerialNumber);

        words[0x10] = 0x0000;                 // execution delay
        words[0x11] = 0x0000;                 // port0 delay
        words[0x12] = 0x0000;                 // port1 delay
        words[0x13] = 0x0000;

        words[Sii.BootRxMailboxWord] = o.RxMailboxOffset;
        words[Sii.BootRxMailboxWord + 1] = o.RxMailboxSize;
        words[Sii.BootTxMailboxWord] = o.TxMailboxOffset;
        words[Sii.BootTxMailboxWord + 1] = o.TxMailboxSize;
        words[Sii.StdRxMailboxWord] = o.RxMailboxOffset;
        words[Sii.StdRxMailboxWord + 1] = o.RxMailboxSize;
        words[Sii.StdTxMailboxWord] = o.TxMailboxOffset;
        words[Sii.StdTxMailboxWord + 1] = o.TxMailboxSize;
        words[Sii.MailboxProtocolWord] = 0x0004;   // CoE

        words[0x3E] = 0x000F;                 // EEPROM size (16 Kbit)
        words[0x3F] = 0x0001;                 // version

        // 类别区
        AddStrings(words, o.Name);
        AddGeneral(words);
        AddFmmu(words);
        AddSyncManagers(words, o);
        AddPdo(words, Sii.CatTxPdo, o.TxPdos);
        AddPdo(words, Sii.CatRxPdo, o.RxPdos);
        if (o.SupportsDc)
            AddDc(words, o);

        // 结束标记
        words.Add(0xFFFF);
        words.Add(0x0000);

        var bytes = new byte[words.Count * 2];
        for (int i = 0; i < words.Count; i++)
        {
            bytes[i * 2] = (byte)(words[i] & 0xFF);
            bytes[i * 2 + 1] = (byte)(words[i] >> 8);
        }
        return bytes;
    }

    private static void SetUInt32(List<ushort> words, int wordAddress, uint value)
    {
        words[wordAddress] = (ushort)(value & 0xFFFF);
        words[wordAddress + 1] = (ushort)(value >> 16);
    }

    private static void AddCategory(List<ushort> words, int type, List<ushort> data)
    {
        words.Add((ushort)type);
        words.Add((ushort)data.Count);
        words.AddRange(data);
    }

    private static void AddStrings(List<ushort> words, string name)
    {
        var data = new List<ushort>();
        var text = System.Text.Encoding.UTF8.GetBytes(name.Length > 250 ? name[..250] : name);
        var raw = new List<byte> { 1, (byte)Math.Min(255, text.Length) };
        raw.AddRange(text);
        if (raw.Count % 2 != 0)
            raw.Add(0);
        for (int i = 0; i < raw.Count; i += 2)
            data.Add((ushort)(raw[i] | (raw[i + 1] << 8)));
        AddCategory(words, Sii.CatStrings, data);
    }

    private static void AddGeneral(List<ushort> words)
    {
        // SOEM 读取位置：ssigen+7=CoE, +8=FoE, +9=EoE, +A=SoE, +D=flags
        var data = new List<byte>
        {
            0x00, 0x00,       // Group(u16)
            0x00,             // Image(u8)
            0x00,             // Order(u8)
            0x01,             // Name index (u8)
            0x07,             // CoE details：使能 SDO/SDO info/PDO assign
            0x01,             // FoE details
            0x00,             // EoE details
            0x00,             // SoE details
            0x00, 0x00, 0x00, // Ebus current / reserved
            0x00             // flags（bit1 = blockLRW）
        };
        AddCategory(words, Sii.CatGeneral, ToWords(data));
    }

    private static void AddFmmu(List<ushort> words)
    {
        // nFMMU(u16) + 每路 FMMU 一个字节的用法
        var data = new List<byte> { 0x02, 0x00, 0x02, 0x01, 0x00, 0x00 };
        AddCategory(words, Sii.CatFmmu, ToWords(data));
    }

    private static void AddSyncManagers(List<ushort> words, Options o)
    {
        var data = new List<byte>();
        var list = o.SyncManagers.Count > 0 ? o.SyncManagers : DefaultSyncManagers(o);
        data.Add((byte)list.Count);
        data.Add(0);
        foreach (var sm in list)
        {
            data.Add((byte)(sm.StartAddress & 0xFF));
            data.Add((byte)(sm.StartAddress >> 8));
            data.Add((byte)(sm.DefaultSize & 0xFF));
            data.Add((byte)(sm.DefaultSize >> 8));
            data.Add(sm.ControlByte);
            data.Add(0x00);       // status
            data.Add((byte)(sm.Enable ? 0x01 : 0x00));
            data.Add(0x00);       // PDI control
        }
        AddCategory(words, Sii.CatSyncManager, ToWords(data));
    }

    public static List<EsiSyncManager> DefaultSyncManagers(Options o)
    {
        int rxSize = Math.Max(1, (o.RxPdos.Sum(p => p.BitLength) + 7) / 8);
        int txSize = Math.Max(1, (o.TxPdos.Sum(p => p.BitLength) + 7) / 8);
        // 输入区起点按输出区大小向上取整到 16 字节，避免大型 PDO 时 SM2/SM3 区域重叠
        int sm3Start = 0x1100 + Math.Max(0x40, (rxSize + 15) & ~15);

        return new List<EsiSyncManager>
        {
            new() { Index = 0, StartAddress = o.RxMailboxOffset, DefaultSize = o.RxMailboxSize, ControlByte = 0x26 },
            new() { Index = 1, StartAddress = o.TxMailboxOffset, DefaultSize = o.TxMailboxSize, ControlByte = 0x22 },
            new() { Index = 2, StartAddress = 0x1100, DefaultSize = rxSize, ControlByte = 0x24 },
            new() { Index = 3, StartAddress = (ushort)sm3Start, DefaultSize = txSize, ControlByte = 0x20 }
        };
    }

    private static void AddPdo(List<ushort> words, int category, List<EsiPdo> pdos)
    {
        var data = new List<byte>();
        data.Add((byte)pdos.Count);
        data.Add(0);
        foreach (var pdo in pdos)
        {
            data.Add((byte)(pdo.Index & 0xFF));
            data.Add((byte)(pdo.Index >> 8));
            data.Add((byte)pdo.Entries.Count);
            data.Add((byte)(pdo.SyncManager >= 0 ? pdo.SyncManager : (category == Sii.CatRxPdo ? 2 : 3)));
            data.Add(0x00); data.Add(0x00);   // name index
            data.Add(0x00); data.Add(0x00);   // flags
            foreach (var e in pdo.Entries)
            {
                data.Add((byte)(e.Index & 0xFF));
                data.Add((byte)(e.Index >> 8));
                data.Add(e.SubIndex);
                data.Add(0x00); data.Add(0x00);   // name index
                data.Add((byte)Math.Max(0, Math.Min(255, e.BitLength)));
                data.Add(0x00);
            }
        }
        AddCategory(words, category, ToWords(data));
    }

    private static void AddDc(List<ushort> words, Options o)
    {
        var data = new byte[8 + 4 + 4 + 4 + 4 + 4 + 4];
        int i = 0;
        data[i++] = (byte)(o.AssignActivate & 0xFF);
        data[i++] = (byte)(o.AssignActivate >> 8);
        data[i++] = 0; data[i++] = 0;
        data[i++] = 1; data[i++] = 0; data[i++] = 0; data[i++] = 0;
        void Put32(uint v)
        {
            data[i++] = (byte)(v & 0xFF);
            data[i++] = (byte)((v >> 8) & 0xFF);
            data[i++] = (byte)((v >> 16) & 0xFF);
            data[i++] = (byte)((v >> 24) & 0xFF);
        }
        Put32((uint)o.CycleTimeSync0);
        Put32((uint)o.ShiftTimeSync0);
        Put32(0);
        Put32(0);
        Put32(0);
        AddCategory(words, Sii.CatDistributedClock, ToWords(data.ToList()));
    }

    private static List<ushort> ToWords(List<byte> bytes)
    {
        if (bytes.Count % 2 != 0)
            bytes.Add(0);
        var words = new List<ushort>();
        for (int i = 0; i < bytes.Count; i += 2)
            words.Add((ushort)(bytes[i] | (bytes[i + 1] << 8)));
        return words;
    }
}

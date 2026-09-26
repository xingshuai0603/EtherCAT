using EtherCAT.Esi;
using EtherCAT.Protocol;

namespace EtherCAT.Master;

/// <summary>PDO 中的一个对象</summary>
public sealed class PdoEntry
{
    public ushort Index { get; set; }
    public byte SubIndex { get; set; }
    public int BitLength { get; set; }
    public string Name { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;
    /// <summary>在所属 PDO 内的位偏移</summary>
    public int BitOffset { get; set; }

    public override string ToString() => $"0x{Index:X4}:{(SubIndex == 0 ? "-" : SubIndex.ToString())} {Name} [{BitLength}]";
}

/// <summary>一个 PDO（RxPdo = 主站→从站输出，TxPdo = 从站→主站输入）</summary>
public sealed class PdoMapping
{
    public ushort Index { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<PdoEntry> Entries { get; } = new();

    public int BitLength => Entries.Sum(e => e.BitLength);
    public int ByteLength => (BitLength + 7) / 8;

    public void RecalculateOffsets()
    {
        int offset = 0;
        foreach (var e in Entries)
        {
            e.BitOffset = offset;
            offset += e.BitLength;
        }
    }
}

/// <summary>PDO 映射来源</summary>
public enum PdoSource
{
    None,
    /// <summary>来自 ESI 描述文件</summary>
    Esi,
    /// <summary>来自从站 EEPROM（SII）</summary>
    Sii,
    /// <summary>通过 CoE 动态读取（0x1C12/0x1C13 + 0x16xx/0x1Axx）</summary>
    Coe
}

/// <summary>
/// 一个 EtherCAT 从站的完整信息
/// </summary>
public sealed class SlaveInfo
{
    /// <summary>从站序号（1-based，与 SOEM 一致）</summary>
    public int SlaveIndex { get; init; }
    /// <summary>配置站地址</summary>
    public ushort ConfiguredAddress { get; set; }
    /// <summary>自动增量地址（ADP）</summary>
    public ushort AutoIncrementAddress => Esc.AutoIncrementAddress(SlaveIndex);

    public uint VendorId { get; set; }
    public uint ProductCode { get; set; }
    public uint RevisionNumber { get; set; }
    public uint SerialNumber { get; set; }

    public string Name { get; set; } = string.Empty;
    public string VendorName { get; set; } = string.Empty;
    public string ProductName => string.IsNullOrWhiteSpace(Name) ? $"0x{ProductCode:X8}" : Name;

    public AlState State { get; set; } = AlState.Init;
    public bool ErrorIndicator { get; set; }
    public ushort AlStatusCodeValue { get; set; }
    public string AlStatusText => ErrorIndicator ? AlStatusCode.Describe(AlStatusCodeValue) : State.ToString();

    /// <summary>命中的 ESI 描述</summary>
    public EsiDevice? Esi { get; set; }

    // 邮箱
    public bool HasMailbox { get; set; }
    public ushort MailboxWriteOffset { get; set; }
    public ushort MailboxWriteSize { get; set; }
    public ushort MailboxReadOffset { get; set; }
    public ushort MailboxReadSize { get; set; }
    public ushort MailboxProtocol { get; set; }
    public bool SupportsCoe => HasMailbox && (MailboxProtocol & 0x04) != 0;

    /// <summary>SII（EEPROM）中的同步管理器配置</summary>
    public List<SiiSyncManager> SyncManagerInfo { get; set; } = new();
    /// <summary>SII 中的 TxPdo 概要</summary>
    public List<SiiPdoInfo> SiiTxPdos { get; set; } = new();
    /// <summary>SII 中的 RxPdo 概要</summary>
    public List<SiiPdoInfo> SiiRxPdos { get; set; } = new();

    // 过程数据
    public PdoMapping? RxPdo { get; set; }
    public PdoMapping? TxPdo { get; set; }
    public PdoSource PdoSource { get; set; } = PdoSource.None;

    public ushort Sm2PhysicalStart { get; set; }
    public ushort Sm3PhysicalStart { get; set; }
    public int OutputByteOffset { get; set; }
    public int InputByteOffset { get; set; }
    public int OutputBitSize { get; set; }
    public int InputBitSize { get; set; }

    public int OutputByteLength => (OutputBitSize + 7) / 8;
    public int InputByteLength => (InputBitSize + 7) / 8;

    /// <summary>输出变量（主站→从站）</summary>
    public List<PdoVariable> Outputs { get; } = new();
    /// <summary>输入变量（从站→主站）</summary>
    public List<PdoVariable> Inputs { get; } = new();

    public bool SupportsDc { get; set; }
    public ushort DcAssignActivate { get; set; }
    public int DcCycleTimeSync0 { get; set; }

    /// <summary>是否为 CiA402 伺服驱动器</summary>
    public bool IsDrive { get; set; }
    /// <summary>是否为 IO 模块</summary>
    public bool IsIoModule { get; set; }

    public override string ToString() =>
        $"#{SlaveIndex} {ProductName} (0x{VendorId:X8}/0x{ProductCode:X8}) {State}";
}

/// <summary>
/// 绑定到过程数据镜像上的 PDO 变量
/// </summary>
public sealed class PdoVariable
{
    private readonly ProcessImage _image;

    internal PdoVariable(ProcessImage image, PdoEntry entry, int absoluteBitOffset, bool isOutput)
    {
        _image = image;
        Entry = entry;
        AbsoluteBitOffset = absoluteBitOffset;
        IsOutput = isOutput;
    }

    public PdoEntry Entry { get; }
    public int AbsoluteBitOffset { get; }
    public bool IsOutput { get; }

    public ushort Index => Entry.Index;
    public byte SubIndex => Entry.SubIndex;
    public int BitLength => Entry.BitLength;
    public string Name => Entry.Name;
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? $"0x{Index:X4}" : Name;

    /// <summary>原始无符号值</summary>
    public ulong RawValue
    {
        get => _image.ReadBits(AbsoluteBitOffset, BitLength);
        set => _image.WriteBits(AbsoluteBitOffset, BitLength, value);
    }

    /// <summary>按有符号数解释（16/32 位）</summary>
    public int SignedValue
    {
        get
        {
            ulong raw = RawValue;
            return BitLength switch
            {
                <= 8 => unchecked((sbyte)(byte)raw),
                <= 16 => unchecked((short)(ushort)raw),
                _ => unchecked((int)(uint)raw)
            };
        }
        set => RawValue = BitLength <= 8 ? (byte)value : BitLength <= 16 ? (ushort)value : (uint)value;
    }

    public bool BoolValue
    {
        get => RawValue != 0;
        set => RawValue = value ? 1UL : 0UL;
    }

    public override string ToString() => $"{DisplayName} = {RawValue}";
}

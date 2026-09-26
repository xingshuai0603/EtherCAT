namespace EtherCAT.Esi;

/// <summary>
/// ESI 文件中的 PDO 条目（对应 &lt;Entry&gt;）
/// </summary>
public sealed class EsiPdoEntry
{
    public ushort Index { get; set; }
    public byte SubIndex { get; set; }
    public int BitLength { get; set; }
    public string Name { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;

    public override string ToString() => $"0x{Index:X4}:{SubIndex} {Name} ({BitLength} bit)";
}

/// <summary>
/// ESI 文件中的 PDO（RxPdo = 主站输出，TxPdo = 主站输入）
/// </summary>
public sealed class EsiPdo
{
    public ushort Index { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool Fixed { get; set; }
    public bool Mandatory { get; set; }
    /// <summary>绑定的同步管理器编号（2 = 输出，3 = 输入）</summary>
    public int SyncManager { get; set; } = -1;
    public List<EsiPdoEntry> Entries { get; } = new();

    public int BitLength => Entries.Sum(e => e.BitLength);
    public int ByteLength => (BitLength + 7) / 8;

    public override string ToString() => $"0x{Index:X4} {Name} ({BitLength} bit)";
}

/// <summary>
/// ESI 中的同步管理器配置 &lt;Sm&gt;
/// </summary>
public sealed class EsiSyncManager
{
    public int Index { get; set; }
    public ushort StartAddress { get; set; }
    public byte ControlByte { get; set; }
    public int DefaultSize { get; set; }
    public int MinSize { get; set; }
    public int MaxSize { get; set; }
    public bool Enable { get; set; } = true;
}

/// <summary>
/// 一个 ESI 设备（&lt;Device&gt; 节点）
/// </summary>
public sealed class EsiDevice
{
    public string SourceFile { get; set; } = string.Empty;
    public uint VendorId { get; set; }
    public string VendorName { get; set; } = string.Empty;
    public uint ProductCode { get; set; }
    public uint RevisionNumber { get; set; }
    public string Name { get; set; } = string.Empty;
    public string TypeText { get; set; } = string.Empty;

    public List<EsiSyncManager> SyncManagers { get; } = new();
    public List<EsiPdo> RxPdos { get; } = new();
    public List<EsiPdo> TxPdos { get; } = new();

    /// <summary>支持分布式时钟</summary>
    public bool SupportsDc { get; set; }
    public ushort AssignActivate { get; set; }
    public int CycleTimeSync0 { get; set; }
    public int ShiftTimeSync0 { get; set; }

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? TypeText : Name;

    public override string ToString() =>
        $"0x{VendorId:X8}/0x{ProductCode:X8} rev 0x{RevisionNumber:X8} - {DisplayName}";

    public EsiSyncManager? GetSyncManager(int index) =>
        SyncManagers.FirstOrDefault(sm => sm.Index == index);
}

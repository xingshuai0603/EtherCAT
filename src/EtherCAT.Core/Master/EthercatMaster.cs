using System.Buffers.Binary;
using System.Diagnostics;
using EtherCAT.Esi;
using EtherCAT.Link;
using EtherCAT.Protocol;

namespace EtherCAT.Master;

/// <summary>
/// EtherCAT 主站：负责从站扫描、PDO 映射配置、状态机切换与周期过程数据收发
/// </summary>
public sealed class EthercatMaster : IDisposable
{
    private readonly IEthercatLink _link;
    private readonly EsiDatabase _esi;
    private readonly IEthercatLog _log;
    private readonly EthercatFrame _frame = new();
    private readonly byte[] _response = new byte[Esc.MaxFrameSize];
    private readonly List<SlaveInfo> _slaves = new();
    private readonly byte[] _sourceMac = { 0x02, 0x01, 0x00, 0x00, 0x00, 0x01 };

    private Thread? _cyclicThread;
    private volatile bool _cyclicRunning;

    public EthercatMaster(IEthercatLink link, EsiDatabase? esi = null, IEthercatLog? log = null)
    {
        _link = link;
        _esi = esi ?? new EsiDatabase();
        _log = log ?? NullLog.Instance;
        Coe = new CoeClient(this);
    }

    public IReadOnlyList<SlaveInfo> Slaves => _slaves;
    public ProcessImage Image { get; } = new();
    public CoeClient Coe { get; }

    /// <summary>周期时间（微秒）</summary>
    public int CycleTimeUs { get; set; } = 1000;
    /// <summary>单次帧收发超时（微秒）</summary>
    public int TimeoutUs { get; set; } = 20000;
    /// <summary>期望工作计数器</summary>
    public int ExpectedWorkingCounter { get; private set; }
    /// <summary>上一周期实际工作计数器</summary>
    public int LastWorkingCounter { get; private set; }
    /// <summary>工作计数器异常次数</summary>
    public int WorkingCounterErrors { get; private set; }
    public bool IsConfigured { get; private set; }
    public bool IsRunning => _cyclicRunning;

    /// <summary>周期结束事件（UI 刷新用）</summary>
    public event Action? CycleCompleted;

    // ------------------------------------------------------------------ 基础收发

    private int Transfer(EthercatCommand command, ushort adp, ushort ado, Span<byte> data)
    {
        lock (_frame)
        {
            _frame.Reset(_sourceMac);
            var datagram = _frame.Add(command, adp, ado, data);
            _frame.Finish();

            int responseLength = _link.Transceive(_frame.Buffer, _frame.Length, _response, TimeoutUs);
            if (responseLength <= 0)
                return 0;

            _frame.TryParseResponse(_response, responseLength);
            if (datagram.Data.Length > 0)
                datagram.Data.AsSpan().CopyTo(data);
            return datagram.WorkingCounter;
        }
    }

    public int Fprd(ushort address, ushort ado, Span<byte> data) => Transfer(EthercatCommand.Fprd, address, ado, data);
    public int Fpwr(ushort address, ushort ado, Span<byte> data) => Transfer(EthercatCommand.Fpwr, address, ado, data);
    public int Aprd(ushort address, ushort ado, Span<byte> data) => Transfer(EthercatCommand.Aprd, address, ado, data);
    public int Apwr(ushort address, ushort ado, Span<byte> data) => Transfer(EthercatCommand.Apwr, address, ado, data);
    public int Brd(ushort ado, Span<byte> data) => Transfer(EthercatCommand.Brd, 0, ado, data);
    public int Bwr(ushort ado, Span<byte> data) => Transfer(EthercatCommand.Bwr, 0, ado, data);

    public SlaveInfo GetSlave(int slaveIndex)
    {
        if (slaveIndex < 1 || slaveIndex > _slaves.Count)
            throw new EthercatException($"从站序号越界：{slaveIndex}（当前共 {_slaves.Count} 个从站）");
        return _slaves[slaveIndex - 1];
    }

    // ------------------------------------------------------------------ EEPROM

    private byte[] EepromRead(ushort configAddress, ushort wordAddress)
    {
        // 确保 EEPROM 由主站（EtherCAT）控制
        var zero = new byte[2];
        Fpwr(configAddress, Esc.EepConfig, zero);

        var statusBuffer = new byte[2];
        Fprd(configAddress, Esc.EepStatus, statusBuffer);
        ushort status = BinaryPrimitives.ReadUInt16LittleEndian(statusBuffer);
        if ((status & Sii.StatusErrorMask) != 0)
        {
            var nop = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(nop, Sii.CommandNop);
            Fpwr(configAddress, Esc.EepControl, nop);
            Fprd(configAddress, Esc.EepStatus, statusBuffer);
        }

        // 发送读命令
        var command = new byte[6];
        BinaryPrimitives.WriteUInt16LittleEndian(command.AsSpan(0, 2), Sii.CommandRead);
        BinaryPrimitives.WriteUInt16LittleEndian(command.AsSpan(2, 2), wordAddress);
        BinaryPrimitives.WriteUInt16LittleEndian(command.AsSpan(4, 2), 0);
        Fpwr(configAddress, Esc.EepControl, command);

        // 等待完成
        var sw = Stopwatch.StartNew();
        status = 0;
        while (sw.ElapsedMilliseconds < 100)
        {
            Fprd(configAddress, Esc.EepStatus, statusBuffer);
            status = BinaryPrimitives.ReadUInt16LittleEndian(statusBuffer);
            if ((status & Sii.StatusBusy) == 0)
                break;
            Thread.SpinWait(50);
        }

        int length = (status & Sii.StatusRead64) != 0 ? 8 : 4;
        var data = new byte[length];
        Fprd(configAddress, Esc.EepData, data);
        return data;
    }

    // ------------------------------------------------------------------ 扫描

    /// <summary>
    /// 扫描网络：广播计数 → 分配站地址 → 读取 SII 身份与邮箱 → 匹配 ESI 描述
    /// </summary>
    public int Scan()
    {
        _slaves.Clear();
        IsConfigured = false;

        var buffer = new byte[2];
        int workingCounter = Brd(Esc.Type, buffer);
        int count = workingCounter;
        if (count <= 0 || count > 512)
        {
            _log.Log($"扫描完成，未发现从站（WKC = {workingCounter}）");
            return 0;
        }

        _log.Log($"扫描到 {count} 个从站，开始读取从站信息…");

        for (int i = 1; i <= count; i++)
        {
            var info = new SlaveInfo { SlaveIndex = i };
            ushort autoIncrement = Esc.AutoIncrementAddress(i);
            ushort configAddress = (ushort)(Esc.NodeOffset + i);

            // 1) 分配配置站地址
            var addressBytes = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(addressBytes, configAddress);
            Apwr(autoIncrement, Esc.StationAddress, addressBytes);
            info.ConfiguredAddress = configAddress;

            // 2) 读 AL 状态
            ReadAlStatus(info);

            // 3) 读 EEPROM
            ushort capturedAddress = configAddress;
            var sii = new SiiReader(word => EepromRead(capturedAddress, word));
            try
            {
                var identity = sii.ReadIdentity();
                info.VendorId = identity.VendorId;
                info.ProductCode = identity.ProductCode;
                info.RevisionNumber = identity.RevisionNumber;
                info.SerialNumber = identity.SerialNumber;

                var mailbox = sii.ReadMailbox();
                if (mailbox.WriteOffset != 0 && mailbox.WriteSize != 0)
                {
                    info.HasMailbox = true;
                    info.MailboxWriteOffset = mailbox.WriteOffset;
                    info.MailboxWriteSize = mailbox.WriteSize;
                    info.MailboxReadOffset = mailbox.ReadOffset;
                    info.MailboxReadSize = mailbox.ReadSize;
                    info.MailboxProtocol = mailbox.Protocol;
                }

                info.Name = sii.ReadString(1);
                info.SyncManagerInfo = sii.ReadSyncManagers();
                info.SiiTxPdos = sii.ReadPdos(Sii.CatTxPdo);
                info.SiiRxPdos = sii.ReadPdos(Sii.CatRxPdo);
            }
            catch (Exception ex)
            {
                _log.Log($"从站 #{i} 读取 EEPROM 失败：{ex.Message}");
            }

            // 4) 匹配 ESI 描述文件
            info.Esi = _esi.Find(info.VendorId, info.ProductCode, info.RevisionNumber);
            if (info.Esi != null)
            {
                if (string.IsNullOrWhiteSpace(info.Name))
                    info.Name = info.Esi.DisplayName;
                info.VendorName = info.Esi.VendorName;
                info.SupportsDc = info.Esi.SupportsDc;
                info.DcAssignActivate = info.Esi.AssignActivate;
                info.DcCycleTimeSync0 = info.Esi.CycleTimeSync0;
            }

            ClassifySlave(info);
            _slaves.Add(info);
            _log.Log($"#{i} {info.ProductName} 厂商=0x{info.VendorId:X8} 产品=0x{info.ProductCode:X8} " +
                     $"版本=0x{info.RevisionNumber:X8} 序列号=0x{info.SerialNumber:X8}" +
                     (info.HasMailbox ? " [CoE]" : "") + (info.Esi != null ? " [ESI 匹配]" : " [无 ESI]"));
        }

        _log.Log($"扫描完成，共 {_slaves.Count} 个从站");
        return _slaves.Count;
    }

    /// <summary>刷新全部从站的 AL 状态与状态码（UI 轮询用）</summary>
    public void RefreshSlaveStates()
    {
        foreach (var slave in _slaves)
            ReadAlStatus(slave);
    }

    private void ReadAlStatus(SlaveInfo info)
    {
        var buffer = new byte[2];
        Fprd(info.ConfiguredAddress, Esc.AlStatus, buffer);
        ushort value = BinaryPrimitives.ReadUInt16LittleEndian(buffer);
        info.State = (AlState)(value & 0x0F);
        info.ErrorIndicator = (value & 0x10) != 0;

        var codeBuffer = new byte[2];
        Fprd(info.ConfiguredAddress, Esc.AlStatusCode, codeBuffer);
        info.AlStatusCodeValue = BinaryPrimitives.ReadUInt16LittleEndian(codeBuffer);
    }

    private static void ClassifySlave(SlaveInfo info)
    {
        var indices = new HashSet<ushort>();
        foreach (var pdo in info.Esi?.RxPdos.Concat(info.Esi.TxPdos) ?? Enumerable.Empty<EsiPdo>())
            foreach (var e in pdo.Entries)
                indices.Add(e.Index);

        if (indices.Contains(0x6040) && indices.Contains(0x6041))
            info.IsDrive = true;

        // CiA401 数字量 IO：0x6000（输入）/0x7000（输出）系列，驱动器自身不算 IO 模块
        bool hasDigitalObjects = indices.Contains(0x6000) || indices.Contains(0x7000) ||
                                 indices.Any(i => (i & 0xFF00) == 0x6200) || indices.Any(i => (i & 0xFF00) == 0x6300);

        // 模拟量模块：0x640x 输入 / 0x641x 输出
        bool hasAnalogObjects = indices.Any(i => (i & 0xFFF0) == 0x6400) ||
                                indices.Any(i => (i & 0xFFF0) == 0x6410);

        info.IsIoModule = (hasDigitalObjects || hasAnalogObjects) && !info.IsDrive;
    }

    // ------------------------------------------------------------------ 配置

    /// <summary>
    /// 建立 PDO 映射、编程 SM/FMMU，并把网络推进到 Operational
    /// </summary>
    public bool Configure(bool useDistributedClock = false)
    {
        if (_slaves.Count == 0)
        {
            _log.Log("没有从站可配置，请先扫描");
            return false;
        }

        // 1) 建立 PDO 映射
        int logicalOffset = 0;
        foreach (var slave in _slaves)
        {
            BuildPdoMapping(slave);

            slave.OutputBitSize = slave.RxPdo?.BitLength ?? 0;
            slave.OutputByteOffset = logicalOffset;
            logicalOffset += (slave.OutputBitSize + 7) / 8;

            slave.InputBitSize = slave.TxPdo?.BitLength ?? 0;
            slave.InputByteOffset = logicalOffset;
            logicalOffset += (slave.InputBitSize + 7) / 8;
        }

        Image.Resize(Math.Max(1, logicalOffset));
        _log.Log($"过程数据镜像大小：{logicalOffset} 字节");

        // 2) 请求 PREOP（邮箱通信需要）
        RequestStateAll(AlState.PreOperational);

        // 3) 必要时通过 CoE 写入 PDO 分配
        foreach (var slave in _slaves)
            ApplyPdoAssignment(slave);

        // 4) 编程同步管理器与 FMMU
        foreach (var slave in _slaves)
        {
            ProgramSyncManagers(slave);
            ProgramFmmus(slave);
            if (useDistributedClock && slave.SupportsDc)
                ProgramDistributedClock(slave);
        }

        // 5) 建立变量绑定
        BuildVariables();

        // 6) 期望工作计数器：输入 +1，输出 +2
        ExpectedWorkingCounter = 0;
        foreach (var slave in _slaves)
        {
            if (slave.InputBitSize > 0) ExpectedWorkingCounter += 1;
            if (slave.OutputBitSize > 0) ExpectedWorkingCounter += 2;
        }

        // 7) 推进到 SAFEOP / OP
        bool safeOp = RequestStateAll(AlState.SafeOperational);
        bool operational = safeOp && RequestStateAll(AlState.Operational);

        IsConfigured = operational;
        foreach (var slave in _slaves)
            ReadAlStatus(slave);

        if (!operational)
        {
            foreach (var slave in _slaves.Where(s => s.ErrorIndicator))
                _log.Log($"从站 #{slave.SlaveIndex} 未能进入 OP：{AlStatusCode.Describe(slave.AlStatusCodeValue)}");
        }
        else
        {
            _log.Log($"网络已进入 OP，期望 WKC = {ExpectedWorkingCounter}");
        }

        return operational;
    }

    private void BuildPdoMapping(SlaveInfo slave)
    {
        // 1) ESI 描述文件优先
        if (slave.Esi != null && (slave.Esi.RxPdos.Count > 0 || slave.Esi.TxPdos.Count > 0))
        {
            slave.RxPdo = BuildPdoFromEsi(slave.Esi.RxPdos, 0x1600, "RxPdo(ESI)");
            slave.TxPdo = BuildPdoFromEsi(slave.Esi.TxPdos, 0x1A00, "TxPdo(ESI)");
            slave.PdoSource = PdoSource.Esi;
            return;
        }

        // 2) 通过 CoE 读取当前 PDO 分配（动态映射）
        if (slave.SupportsCoe)
        {
            try
            {
                var rx = BuildPdoFromCoe(slave, EtherCAT.Protocol.Coe.RxPdoAssign, EtherCAT.Protocol.Coe.RxPdoMappingBase, "RxPdo(CoE)");
                var tx = BuildPdoFromCoe(slave, EtherCAT.Protocol.Coe.TxPdoAssign, EtherCAT.Protocol.Coe.TxPdoMappingBase, "TxPdo(CoE)");
                if (rx != null || tx != null)
                {
                    slave.RxPdo = rx;
                    slave.TxPdo = tx;
                    slave.PdoSource = PdoSource.Coe;
                    return;
                }
            }
            catch (Exception ex)
            {
                _log.Log($"从站 #{slave.SlaveIndex} CoE 读取 PDO 映射失败：{ex.Message}");
            }
        }

        // 3) 退回到 EEPROM 中的 PDO 描述（仅能得到长度，条目未知）
        if (slave.SiiRxPdos.Count > 0 || slave.SiiTxPdos.Count > 0)
        {
            slave.RxPdo = BuildPdoFromSii(slave.SiiRxPdos, "RxPdo(SII)");
            slave.TxPdo = BuildPdoFromSii(slave.SiiTxPdos, "TxPdo(SII)");
            slave.PdoSource = PdoSource.Sii;
        }
    }

    private static PdoMapping BuildPdoFromEsi(List<EsiPdo> pdos, ushort defaultIndex, string name)
    {
        var mapping = new PdoMapping { Index = pdos.FirstOrDefault()?.Index ?? defaultIndex, Name = name };
        foreach (var pdo in pdos)
        {
            foreach (var entry in pdo.Entries)
            {
                int bitLength = entry.BitLength > 0 ? entry.BitLength : DataTypeBitLength(entry.DataType);
                mapping.Entries.Add(new PdoEntry
                {
                    Index = entry.Index,
                    SubIndex = entry.SubIndex,
                    BitLength = bitLength,
                    Name = entry.Name,
                    DataType = entry.DataType
                });
            }
        }
        mapping.RecalculateOffsets();
        return mapping;
    }

    private PdoMapping? BuildPdoFromCoe(SlaveInfo slave, ushort assignIndex, ushort mappingBase, string name)
    {
        byte assignedCount = Coe.ReadByte(slave.SlaveIndex, assignIndex, 0);
        if (assignedCount == 0 || assignedCount == 0xFF)
            return null;

        var mapping = new PdoMapping { Name = name };
        for (byte i = 1; i <= assignedCount; i++)
        {
            ushort pdoIndex = Coe.ReadUInt16(slave.SlaveIndex, assignIndex, i);
            if (pdoIndex == 0)
                continue;
            mapping.Index = pdoIndex;

            ushort mapIndex = (ushort)(mappingBase + (pdoIndex & 0x00FF));
            byte entryCount = Coe.ReadByte(slave.SlaveIndex, mapIndex, 0);
            for (byte e = 1; e <= entryCount; e++)
            {
                uint mapped = Coe.ReadUInt32(slave.SlaveIndex, mapIndex, e);
                if (mapped == 0)
                    continue;
                ushort objectIndex = (ushort)(mapped >> 16);
                byte subIndex = (byte)((mapped >> 8) & 0xFF);
                int bitLength = (int)(mapped & 0xFF);
                mapping.Entries.Add(new PdoEntry
                {
                    Index = objectIndex,
                    SubIndex = subIndex,
                    BitLength = bitLength,
                    Name = $"0x{objectIndex:X4}"
                });
            }
        }

        if (mapping.Entries.Count == 0)
            return null;

        mapping.RecalculateOffsets();
        return mapping;
    }

    private static PdoMapping BuildPdoFromSii(List<SiiPdoInfo> pdos, string name)
    {
        var mapping = new PdoMapping { Name = name, Index = pdos.FirstOrDefault()?.Index ?? 0 };
        // EEPROM 只给出 PDO 的总体位长，具体条目未知，建模成一个占位域
        foreach (var pdo in pdos)
        {
            if (pdo.BitSize <= 0)
                continue;
            mapping.Entries.Add(new PdoEntry
            {
                Index = pdo.Index,
                SubIndex = 0,
                BitLength = pdo.BitSize,
                Name = $"PDO 0x{pdo.Index:X4}"
            });
        }
        mapping.RecalculateOffsets();
        return mapping;
    }

    private static int DataTypeBitLength(string dataType) => dataType?.ToUpperInvariant() switch
    {
        "BOOL" or "BOOLEAN" => 1,
        "SINT" or "INT8" or "USINT" or "UINT8" or "BYTE" => 8,
        "INT" or "INT16" or "UINT" or "UINT16" or "WORD" => 16,
        "DINT" or "INT32" or "UDINT" or "UINT32" or "DWORD" or "REAL" => 32,
        "LINT" or "INT64" or "ULINT" or "UINT64" => 64,
        _ => 16
    };

    /// <summary>当 ESI 中的 PDO 与从站当前分配不一致时，通过 CoE 写 PDO 分配对象</summary>
    private void ApplyPdoAssignment(SlaveInfo slave)
    {
        if (slave.Esi == null || !slave.SupportsCoe)
            return;

        ApplyOne(slave, EtherCAT.Protocol.Coe.RxPdoAssign, slave.Esi.RxPdos);
        ApplyOne(slave, EtherCAT.Protocol.Coe.TxPdoAssign, slave.Esi.TxPdos);

        void ApplyOne(SlaveInfo s, ushort assignIndex, List<EsiPdo> pdos)
        {
            if (pdos.Count == 0)
                return;
            try
            {
                byte current = Coe.ReadByte(s.SlaveIndex, assignIndex, 0);
                if (current == pdos.Count)
                    return;

                Coe.WriteByte(s.SlaveIndex, assignIndex, 0, 0);            // 清空
                byte subIndex = 1;
                foreach (var pdo in pdos)
                    Coe.WriteUInt16(s.SlaveIndex, assignIndex, subIndex++, pdo.Index);
                Coe.WriteByte(s.SlaveIndex, assignIndex, 0, (byte)pdos.Count);
                _log.Log($"从站 #{s.SlaveIndex} 已通过 CoE 写入 0x{assignIndex:X4} PDO 分配（{pdos.Count} 个）");
            }
            catch (Exception ex)
            {
                _log.Log($"从站 #{s.SlaveIndex} 写入 PDO 分配 0x{assignIndex:X4} 失败：{ex.Message}");
            }
        }
    }

    private void ProgramSyncManagers(SlaveInfo slave)
    {
        ushort sm2Start = slave.Sm2PhysicalStart != 0 ? slave.Sm2PhysicalStart : (ushort)0x1100;
        ushort sm3Start = slave.Sm3PhysicalStart != 0 ? slave.Sm3PhysicalStart : (ushort)0x1140;

        // 优先使用 SII / ESI 中声明的物理地址
        var fromSii = slave.SyncManagerInfo;
        if (fromSii != null && fromSii.Count > 3)
        {
            sm2Start = fromSii[2].PhysicalStart != 0 ? fromSii[2].PhysicalStart : sm2Start;
            sm3Start = fromSii[3].PhysicalStart != 0 ? fromSii[3].PhysicalStart : sm3Start;
        }
        else if (slave.Esi != null)
        {
            var sm2 = slave.Esi.GetSyncManager(2);
            var sm3 = slave.Esi.GetSyncManager(3);
            if (sm2 != null && sm2.StartAddress != 0) sm2Start = sm2.StartAddress;
            if (sm3 != null && sm3.StartAddress != 0) sm3Start = sm3.StartAddress;
        }

        // 邮箱 SM0/SM1
        if (slave.HasMailbox)
        {
            WriteSyncManager(slave, 0, slave.MailboxWriteOffset, slave.MailboxWriteSize, 0x26);
            WriteSyncManager(slave, 1, slave.MailboxReadOffset, slave.MailboxReadSize, 0x22);
        }

        // 过程数据 SM2（输出）/ SM3（输入）
        if (slave.OutputBitSize > 0)
            WriteSyncManager(slave, 2, sm2Start, (ushort)Math.Max(1, slave.OutputByteLength), 0x24);
        if (slave.InputBitSize > 0)
            WriteSyncManager(slave, 3, sm3Start, (ushort)Math.Max(1, slave.InputByteLength), 0x20);

        slave.Sm2PhysicalStart = sm2Start;
        slave.Sm3PhysicalStart = sm3Start;
    }

    private void WriteSyncManager(SlaveInfo slave, int index, ushort startAddress, ushort length, byte control)
    {
        var data = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0, 2), startAddress);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2, 2), length);
        data[4] = control;
        data[5] = 0;
        data[6] = 1;   // activate
        data[7] = 0;
        Fpwr(slave.ConfiguredAddress, Esc.Sm(index), data);
    }

    private void ProgramFmmus(SlaveInfo slave)
    {
        if (slave.OutputBitSize > 0)
        {
            var data = new byte[16];
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0, 4), (uint)slave.OutputByteOffset);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4, 2), (ushort)Math.Max(1, slave.OutputByteLength));
            data[6] = 0;                                       // 逻辑起始位
            data[7] = (byte)((slave.OutputBitSize - 1) & 0x07); // 逻辑结束位
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8, 2), slave.Sm2PhysicalStart);
            data[10] = 0;
            data[11] = 0x02;   // 写
            data[12] = 0x01;   // 使能
            Fpwr(slave.ConfiguredAddress, Esc.Fmmu(0), data);
        }

        if (slave.InputBitSize > 0)
        {
            var data = new byte[16];
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0, 4), (uint)slave.InputByteOffset);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4, 2), (ushort)Math.Max(1, slave.InputByteLength));
            data[6] = 0;
            data[7] = (byte)((slave.InputBitSize - 1) & 0x07);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8, 2), slave.Sm3PhysicalStart);
            data[10] = 0;
            data[11] = 0x01;   // 读
            data[12] = 0x01;
            Fpwr(slave.ConfiguredAddress, Esc.Fmmu(1), data);
        }
    }

    private void ProgramDistributedClock(SlaveInfo slave)
    {
        uint cycleTimeNs = (uint)(CycleTimeUs * 1000);
        if (slave.DcCycleTimeSync0 > 0)
            cycleTimeNs = (uint)slave.DcCycleTimeSync0;

        var cycle = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(cycle, cycleTimeNs);
        Fpwr(slave.ConfiguredAddress, Esc.DcCycle0, cycle);

        var activation = new byte[1];
        activation[0] = slave.DcAssignActivate != 0 ? (byte)slave.DcAssignActivate : (byte)0x03;
        Fpwr(slave.ConfiguredAddress, Esc.DcSyncActivation, activation);
    }

    private void BuildVariables()
    {
        foreach (var slave in _slaves)
        {
            slave.Outputs.Clear();
            slave.Inputs.Clear();

            if (slave.RxPdo != null)
            {
                foreach (var entry in slave.RxPdo.Entries)
                {
                    slave.Outputs.Add(new PdoVariable(Image, entry,
                        slave.OutputByteOffset * 8 + entry.BitOffset, true));
                }
            }

            if (slave.TxPdo != null)
            {
                foreach (var entry in slave.TxPdo.Entries)
                {
                    slave.Inputs.Add(new PdoVariable(Image, entry,
                        slave.InputByteOffset * 8 + entry.BitOffset, false));
                }
            }
        }
    }

    // ------------------------------------------------------------------ 状态机

    public bool RequestState(int slaveIndex, AlState target, bool acknowledgeError = false, int timeoutMs = 3000)
    {
        var slave = GetSlave(slaveIndex);
        ushort value = (ushort)((byte)target | (acknowledgeError ? 0x10 : 0));
        var data = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(data, value);
        Fpwr(slave.ConfiguredAddress, Esc.AlControl, data);

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            ReadAlStatus(slave);
            if (slave.State == target)
                return true;
            if (slave.ErrorIndicator)
                return false;
            Thread.Sleep(2);
        }

        return slave.State == target;
    }

    public bool RequestStateAll(AlState target, int timeoutMs = 3000)
    {
        foreach (var slave in _slaves)
        {
            ushort value = (ushort)((byte)target | 0x10);   // 顺带确认错误
            var data = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(data, value);
            Fpwr(slave.ConfiguredAddress, Esc.AlControl, data);
        }

        bool allOk = true;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            allOk = true;
            foreach (var slave in _slaves)
            {
                ReadAlStatus(slave);
                if (slave.State != target)
                    allOk = false;
            }
            if (allOk)
                break;
            Thread.Sleep(5);
        }

        return allOk;
    }

    // ------------------------------------------------------------------ 周期过程数据

    private const int ProcessDataPayloadOffset =
        Esc.EthernetHeaderSize + Esc.EthercatHeaderSize + Esc.DatagramHeaderSize;

    /// <summary>发送/接收一个周期的过程数据（LRW）</summary>
    public bool SendProcessData()
    {
        if (!IsConfigured)
            return false;

        int length = Image.Size;
        lock (_frame)
        {
            // 直接构造帧，避免每周期分配内存
            var buffer = _frame.Buffer;
            Array.Clear(buffer, 0, ProcessDataPayloadOffset + length + Esc.WorkCounterSize);
            buffer.AsSpan(0, 6).Fill(0xFF);
            _sourceMac.CopyTo(buffer, 6);
            buffer[12] = 0x88;
            buffer[13] = 0xA4;

            int headerLength = Esc.DatagramHeaderSize + length + Esc.WorkCounterSize;
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(Esc.EthernetHeaderSize, 2),
                (ushort)((headerLength & 0x7FF) | (1 << 12)));

            int pos = Esc.EthernetHeaderSize + Esc.EthercatHeaderSize;
            buffer[pos++] = (byte)EthercatCommand.Lrw;
            buffer[pos++] = 1;
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(pos, 2), 0); pos += 2;   // 逻辑地址低 16 位
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(pos, 2), 0); pos += 2;   // 逻辑地址高 16 位
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(pos, 2), (ushort)length); pos += 2;
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(pos, 2), 0); pos += 2;   // IRQ
            Image.Buffer.AsSpan(0, length).CopyTo(buffer.AsSpan(pos, length));
            pos += length;
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(pos, 2), 0);
            pos += Esc.WorkCounterSize;

            int responseLength = _link.Transceive(buffer, pos, _response, Math.Max(2000, CycleTimeUs * 4));
            if (responseLength <= 0)
            {
                WorkingCounterErrors++;
                return false;
            }

            _response.AsSpan(ProcessDataPayloadOffset, length).CopyTo(Image.Buffer.AsSpan(0, length));
            LastWorkingCounter = BinaryPrimitives.ReadUInt16LittleEndian(
                _response.AsSpan(ProcessDataPayloadOffset + length, 2));
        }

        if (LastWorkingCounter != ExpectedWorkingCounter)
        {
            WorkingCounterErrors++;
            return false;
        }

        CycleCompleted?.Invoke();
        return true;
    }

    /// <summary>启动周期任务</summary>
    public void StartCyclic()
    {
        if (_cyclicRunning)
            return;
        _cyclicRunning = true;
        _cyclicThread = new Thread(CyclicLoop)
        {
            IsBackground = true,
            Name = "EtherCAT Cyclic",
            Priority = ThreadPriority.Highest
        };
        _cyclicThread.Start();
    }

    public void StopCyclic()
    {
        _cyclicRunning = false;
        _cyclicThread?.Join(500);
        _cyclicThread = null;
    }

    private void CyclicLoop()
    {
        var sw = Stopwatch.StartNew();
        long periodTicks = Stopwatch.Frequency * CycleTimeUs / 1_000_000L;
        long next = sw.ElapsedTicks;

        while (_cyclicRunning)
        {
            next += periodTicks;
            try
            {
                SendProcessData();
            }
            catch (Exception ex)
            {
                _log.Log($"周期任务异常：{ex.Message}");
                Thread.Sleep(5);
            }

            long remaining = next - sw.ElapsedTicks;
            if (remaining > 0)
            {
                double remainingMs = remaining * 1000.0 / Stopwatch.Frequency;
                if (remainingMs > 1.5)
                    Thread.Sleep((int)(remainingMs - 1));
                var spin = new SpinWait();
                while (sw.ElapsedTicks < next)
                    spin.SpinOnce();
            }
            else if (remaining < -periodTicks * 4)
            {
                next = sw.ElapsedTicks;   // 追不上时重新对齐，避免持续追赶
            }
        }
    }

    public void Dispose()
    {
        StopCyclic();
        IsConfigured = false;
    }
}

using System.Buffers.Binary;
using EtherCAT.Esi;
using EtherCAT.Protocol;

namespace EtherCAT.Link.Simulated;

/// <summary>
/// 位级 PDO 数据打包/解包（EtherCAT 过程数据按 LSB 优先逐位紧凑排列）
/// </summary>
public static class PdoBitAccess
{
    public static ulong ReadBits(ReadOnlySpan<byte> buffer, int bitOffset, int bitLength)
    {
        ulong value = 0;
        for (int i = 0; i < bitLength; i++)
        {
            int bit = bitOffset + i;
            if ((buffer[bit >> 3] & (1 << (bit & 7))) != 0)
                value |= 1UL << i;
        }
        return value;
    }

    public static void WriteBits(Span<byte> buffer, int bitOffset, int bitLength, ulong value)
    {
        for (int i = 0; i < bitLength; i++)
        {
            int bit = bitOffset + i;
            int byteIndex = bit >> 3;
            int mask = 1 << (bit & 7);
            if (((value >> i) & 1) != 0)
                buffer[byteIndex] = (byte)(buffer[byteIndex] | mask);
            else
                buffer[byteIndex] = (byte)(buffer[byteIndex] & ~mask);
        }
    }
}

/// <summary>
/// 同步管理器配置（对应 ESC SM 寄存器组）
/// </summary>
public sealed class SyncManagerSetting
{
    public ushort PhysicalStartAddress;
    public ushort Length;
    public byte Control;
    public byte Status;
    public byte Activate;
    public byte PdiControl;

    public bool IsActive => Activate != 0;
    /// <summary>bit2：1 = 主站写入（从站接收）</summary>
    public bool IsWriteDirection => (Control & 0x04) != 0;
}

/// <summary>
/// FMMU 配置（对应 ESC FMMU 寄存器组）
/// </summary>
public sealed class FmmuSetting
{
    public uint LogicalStartAddress;
    public ushort Length;
    public byte LogicalStartBit;
    public byte LogicalEndBit;
    public ushort PhysicalStartAddress;
    public byte PhysicalStartBit;
    public byte Type;      // bit0 = 读, bit1 = 写
    public byte Active;

    public bool IsRead => (Type & 0x01) != 0;
    public bool IsWrite => (Type & 0x02) != 0;
}

/// <summary>
/// 仿真 EtherCAT 从站：实现了 ESC 寄存器、EEPROM(SII)、邮箱(CoE SDO)、SM/FMMU 逻辑寻址，
/// 以及简单的伺服/IO 行为模型。用于在没有真实硬件时验证主站。
/// </summary>
public sealed class SimulatedSlave
{
    private readonly byte[] _dpram = new byte[0x1000];   // 0x1000 - 0x1FFF 用户存储区
    private readonly SyncManagerSetting[] _sm = new SyncManagerSetting[4];
    private readonly FmmuSetting[] _fmmu = new FmmuSetting[4];
    private readonly Dictionary<uint, byte[]> _objects = new();   // key = index<<8 | subindex

    private ushort _eepromStatus;
    private byte _eepromConfig;
    private bool _eepromPdiAccess;
    private byte[] _sii = Array.Empty<byte>();
    private byte _mailboxCounter;

    /// <summary>物理位置（1-based）</summary>
    public int Position { get; }
    public ushort ConfiguredAddress { get; set; }
    public uint VendorId { get; private set; }
    public uint ProductCode { get; private set; }
    public uint RevisionNumber { get; private set; }
    public uint SerialNumber { get; private set; }
    public string Name { get; private set; } = string.Empty;

    public AlState State { get; private set; } = AlState.Init;
    public bool ErrorIndicator { get; private set; }
    public ushort StatusCode { get; private set; }

    public IReadOnlyList<EsiPdo> RxPdoDefinitions { get; }
    public IReadOnlyList<EsiPdo> TxPdoDefinitions { get; }

    /// <summary>
    /// 行为模型。为空时使用内置的简易伺服/IO 模型；设置后由行为模型接管物理量仿真，
    /// 这样可以扩展出大型 IO 模块、多轴伺服等虚拟设备而不改动协议层。
    /// </summary>
    public ISlaveBehavior? Behavior { get; set; }

    private double _lastDelta = 0.001;

    /// <summary>仿真：数字量输入状态</summary>
    public ushort DigitalInputs { get; set; }
    /// <summary>仿真：数字量输出状态</summary>
    public ushort DigitalOutputs { get; private set; }
    /// <summary>仿真：电机实际位置（内部单位）</summary>
    public long ActualPositionInternal { get; private set; }
    /// <summary>仿真：电机实际速度（内部单位/秒）</summary>
    public int ActualVelocityInternal { get; private set; }

    private SimulatedSlave(int position, SiiBuilder.Options options,
        IReadOnlyList<EsiPdo> rxPdos, IReadOnlyList<EsiPdo> txPdos)
    {
        Position = position;
        VendorId = options.VendorId;
        ProductCode = options.ProductCode;
        RevisionNumber = options.RevisionNumber;
        SerialNumber = options.SerialNumber;
        Name = options.Name;
        RxPdoDefinitions = rxPdos;
        TxPdoDefinitions = txPdos;

        for (int i = 0; i < _sm.Length; i++)
            _sm[i] = new SyncManagerSetting();
        for (int i = 0; i < _fmmu.Length; i++)
            _fmmu[i] = new FmmuSetting();

        // 默认 SM 配置（优先使用设备自带的声明，与 SII 保持一致）
        var defaults = options.SyncManagers.Count > 0 ? options.SyncManagers : SiiBuilder.DefaultSyncManagers(options);
        for (int i = 0; i < defaults.Count && i < _sm.Length; i++)
        {
            _sm[i].PhysicalStartAddress = defaults[i].StartAddress;
            _sm[i].Length = (ushort)Math.Max(1, defaults[i].DefaultSize);
            _sm[i].Control = defaults[i].ControlByte;
            _sm[i].Activate = (byte)(i < 2 ? 1 : 0);   // 邮箱默认使能，过程数据由主站配置
        }

        _sii = SiiBuilder.Build(options);
        BuildObjectDictionary(rxPdos, txPdos);
    }

    /// <summary>基于 ESI 设备描述创建仿真从站</summary>
    public static SimulatedSlave FromEsi(EsiDevice device, int position, uint serialNumber = 0)
    {
        var options = new SiiBuilder.Options
        {
            VendorId = device.VendorId,
            ProductCode = device.ProductCode,
            RevisionNumber = device.RevisionNumber,
            SerialNumber = serialNumber == 0 ? (uint)(0x1000 + position) : serialNumber,
            Name = device.DisplayName,
            SupportsDc = device.SupportsDc,
            AssignActivate = device.AssignActivate,
            CycleTimeSync0 = device.CycleTimeSync0,
            ShiftTimeSync0 = device.ShiftTimeSync0
        };
        options.SyncManagers.AddRange(device.SyncManagers);

        var rx = device.RxPdos.Count > 0 ? device.RxPdos : new List<EsiPdo>();
        var tx = device.TxPdos.Count > 0 ? device.TxPdos : new List<EsiPdo>();
        options.RxPdos.AddRange(rx);
        options.TxPdos.AddRange(tx);
        return new SimulatedSlave(position, options, rx, tx);
    }

    // ------------------------------------------------------------------ 对象字典

    private uint ObjectKey(ushort index, byte subIndex) => ((uint)index << 8) | subIndex;

    public bool TryGetObject(ushort index, byte subIndex, out byte[] value) =>
        _objects.TryGetValue(ObjectKey(index, subIndex), out value!);

    public void SetObject(ushort index, byte subIndex, byte[] value) =>
        _objects[ObjectKey(index, subIndex)] = value;

    public void SetObjectValue(ushort index, byte subIndex, ulong value, int size)
    {
        var buffer = new byte[size];
        for (int i = 0; i < size && i < 8; i++)
            buffer[i] = (byte)(value >> (8 * i));
        SetObject(index, subIndex, buffer);
    }

    public ulong GetObjectValue(ushort index, byte subIndex)
    {
        if (!TryGetObject(index, subIndex, out var data))
            return 0;
        ulong value = 0;
        for (int i = 0; i < data.Length && i < 8; i++)
            value |= (ulong)data[i] << (8 * i);
        return value;
    }

    private void BuildObjectDictionary(IReadOnlyList<EsiPdo> rxPdos, IReadOnlyList<EsiPdo> txPdos)
    {
        SetObjectValue(0x1000, 0, 0x00020192, 4);                 // device type
        SetObjectValue(0x1018, 0, 4, 1);
        SetObjectValue(0x1018, 1, VendorId, 4);
        SetObjectValue(0x1018, 2, ProductCode, 4);
        SetObjectValue(0x1018, 3, RevisionNumber, 4);
        SetObjectValue(0x1018, 4, SerialNumber, 4);

        // SM 通信类型：0=未用 1=邮箱写 2=邮箱读 3=输出 4=输入
        SetObjectValue(0x1C00, 0, 4, 1);
        SetObjectValue(0x1C00, 1, 1, 1);
        SetObjectValue(0x1C00, 2, 2, 1);
        SetObjectValue(0x1C00, 3, 3, 1);
        SetObjectValue(0x1C00, 4, 4, 1);

        SetObjectValue(Coe.RxPdoAssign, 0, (ulong)rxPdos.Count, 1);
        for (int i = 0; i < rxPdos.Count; i++)
        {
            SetObjectValue(Coe.RxPdoAssign, (byte)(i + 1), rxPdos[i].Index, 2);
            FillMappingObject(Coe.RxPdoMappingBase, rxPdos[i]);
        }
        SetObjectValue(Coe.TxPdoAssign, 0, (ulong)txPdos.Count, 1);
        for (int i = 0; i < txPdos.Count; i++)
        {
            SetObjectValue(Coe.TxPdoAssign, (byte)(i + 1), txPdos[i].Index, 2);
            FillMappingObject(Coe.TxPdoMappingBase, txPdos[i]);
        }

        // PDO 中出现的对象全部初始化为 0（含 CiA402 / IO 对象）
        foreach (var pdo in rxPdos.Concat(txPdos))
        {
            foreach (var e in pdo.Entries)
            {
                int size = (e.BitLength + 7) / 8;
                if (size <= 0) size = 1;
                if (!TryGetObject(e.Index, e.SubIndex, out _))
                    SetObject(e.Index, e.SubIndex, new byte[size]);
            }
        }

        // CiA402 常用对象（即使没出现在 PDO 里也提供，便于 SDO 访问）
        EnsureObject(0x6040, 0, 2);   // controlword
        EnsureObject(0x6041, 0, 2);   // statusword
        EnsureObject(0x6060, 0, 1);   // modes of operation
        EnsureObject(0x6061, 0, 1);   // modes of operation display
        EnsureObject(0x6064, 0, 4);   // position actual value
        EnsureObject(0x606C, 0, 4);   // velocity actual value
        EnsureObject(0x6077, 0, 2);   // torque actual value
        EnsureObject(0x607A, 0, 4);   // target position
        EnsureObject(0x60FF, 0, 4);   // target velocity
        EnsureObject(0x603F, 0, 2);   // error code
        EnsureObject(0x6062, 0, 4);   // position demand value
        EnsureObject(0x6081, 0, 4);   // profile velocity
        EnsureObject(0x6083, 0, 4);   // profile acceleration
        EnsureObject(0x6084, 0, 4);   // profile deceleration
        EnsureObject(0x6098, 0, 1);   // homing method
        EnsureObject(0x6099, 1, 4);   // homing speed (search switch)
        EnsureObject(0x6099, 2, 4);   // homing speed (search zero)
        EnsureObject(0x6071, 0, 2);   // target torque

        // IO 对象
        EnsureObject(0x6000, 1, 2);   // digital inputs
        EnsureObject(0x7000, 1, 2);   // digital outputs
    }

    /// <summary>若对象不存在则按指定长度创建（虚拟设备用于补齐可 SDO 访问的对象）</summary>
    public void EnsureObject(ushort index, byte subIndex, int size)
    {
        if (!TryGetObject(index, subIndex, out _))
            SetObject(index, subIndex, new byte[size]);
    }

    /// <summary>按对象长度做符号扩展后读取（DINT/INT 用）</summary>
    public long GetObjectSignedValue(ushort index, byte subIndex)
    {
        if (!TryGetObject(index, subIndex, out var data))
            return 0;
        ulong value = 0;
        for (int i = 0; i < data.Length && i < 8; i++)
            value |= (ulong)data[i] << (8 * i);
        return data.Length switch
        {
            1 => unchecked((sbyte)(byte)value),
            2 => unchecked((short)(ushort)value),
            4 => unchecked((int)(uint)value),
            _ => unchecked((long)value)
        };
    }

    /// <summary>行为模型摘要（调试用）</summary>
    public string BehaviorText => Behavior?.Describe() ?? string.Empty;

    private void FillMappingObject(ushort baseIndex, EsiPdo pdo)
    {
        ushort index = (ushort)(baseIndex + (pdo.Index & 0x00FF));
        SetObjectValue(index, 0, (ulong)pdo.Entries.Count, 1);
        for (int i = 0; i < pdo.Entries.Count; i++)
        {
            var e = pdo.Entries[i];
            uint mapped = ((uint)e.Index << 16) | ((uint)e.SubIndex << 8) | (uint)(e.BitLength & 0xFF);
            SetObjectValue(index, (byte)(i + 1), mapped, 4);
        }
    }

    // ------------------------------------------------------------------ 数据报处理

    /// <summary>
    /// 处理一个数据报。frame 为帧缓冲区，payloadOffset 指向数据区，length 为数据长度。
    /// 返回 true 表示该从站响应了此数据报。
    /// </summary>
    public bool ProcessDatagram(EthercatCommand command, ref ushort adp, ushort ado,
        byte[] frame, int payloadOffset, int length, ref int workingCounter)
    {
        bool addressed;
        switch (command)
        {
            case EthercatCommand.Aprd:
            case EthercatCommand.Apwr:
            case EthercatCommand.Aprw:
                // 自动增量地址是负值：从站先把地址 +1，等于 0 时该从站被寻址
                adp++;
                addressed = adp == 0;
                break;
            case EthercatCommand.Fprd:
            case EthercatCommand.Fpwr:
            case EthercatCommand.Fprw:
            case EthercatCommand.Frmw:
                addressed = adp == ConfiguredAddress && ConfiguredAddress != 0;
                break;
            case EthercatCommand.Brd:
            case EthercatCommand.Bwr:
            case EthercatCommand.Brw:
            case EthercatCommand.Armw:
                if (command == EthercatCommand.Armw)
                {
                    adp++;
                    addressed = adp == 0;
                }
                else
                {
                    addressed = true;
                }
                break;
            default:
                return false;
        }

        if (!addressed)
            return false;

        switch (command)
        {
            case EthercatCommand.Aprd:
            case EthercatCommand.Fprd:
            case EthercatCommand.Brd:
                ReadFromSlave(ado, frame, payloadOffset, length);
                workingCounter += 1;
                return true;

            case EthercatCommand.Apwr:
            case EthercatCommand.Fpwr:
            case EthercatCommand.Bwr:
                WriteToSlave(ado, frame, payloadOffset, length);
                workingCounter += 1;
                return true;

            case EthercatCommand.Aprw:
            case EthercatCommand.Fprw:
            case EthercatCommand.Brw:
            {
                var written = new byte[length];
                Array.Copy(frame, payloadOffset, written, 0, length);
                ReadFromSlave(ado, frame, payloadOffset, length);
                WriteToSlave(ado, written, 0, length);
                workingCounter += 3;   // 读 +1，写 +2
                return true;
            }

            case EthercatCommand.Armw:
            case EthercatCommand.Frmw:
                // 读多写：读取本从站数据后，把相同内容写给所有从站（DC 测量用）
                ReadFromSlave(ado, frame, payloadOffset, length);
                workingCounter += 1;
                return true;
        }

        return false;
    }

    /// <summary>逻辑寻址（LRD/LWR/LRW）</summary>
    public void ProcessLogical(bool doRead, bool doWrite, uint logicalAddress,
        byte[] frame, int payloadOffset, int length, ref int workingCounter)
    {
        bool anyWrite = false, anyRead = false;

        foreach (var fmmu in _fmmu)
        {
            if (fmmu.Active == 0 || fmmu.Length == 0)
                continue;
            if ((doWrite && !fmmu.IsWrite) && (doRead && !fmmu.IsRead))
                continue;

            uint fmmuStart = fmmu.LogicalStartAddress;
            uint fmmuEnd = fmmuStart + fmmu.Length;
            uint reqStart = logicalAddress;
            uint reqEnd = logicalAddress + (uint)length;
            uint start = Math.Max(fmmuStart, reqStart);
            uint end = Math.Min(fmmuEnd, reqEnd);
            if (end <= start)
                continue;

            int offsetInRequest = (int)(start - reqStart);
            int offsetInSlave = (int)(start - fmmuStart);
            int count = (int)(end - start);
            int physical = fmmu.PhysicalStartAddress + offsetInSlave;

            if (doWrite && fmmu.IsWrite)
            {
                var data = new byte[count];
                Array.Copy(frame, payloadOffset + offsetInRequest, data, 0, count);
                WritePhysical(physical, data);
                anyWrite = true;
            }

            if (doRead && fmmu.IsRead)
            {
                ReadPhysical(physical, frame, payloadOffset + offsetInRequest, count);
                anyRead = true;
            }
        }

        if (anyRead) workingCounter += 1;
        if (anyWrite) workingCounter += 2;
    }

    private void ReadPhysical(int address, byte[] destination, int offset, int count)
    {
        int baseAddress = address - Esc.DpramBase;
        if (baseAddress < 0 || baseAddress + count > _dpram.Length)
            return;
        OnBeforeInputRead();
        Array.Copy(_dpram, baseAddress, destination, offset, count);
    }

    private void WritePhysical(int address, byte[] data)
    {
        int baseAddress = address - Esc.DpramBase;
        if (baseAddress < 0 || baseAddress + data.Length > _dpram.Length)
            return;
        Array.Copy(data, 0, _dpram, baseAddress, data.Length);
        OnAfterOutputWrite(address, data.Length);
    }

    private void ReadFromSlave(ushort ado, byte[] frame, int offset, int length)
    {
        var buffer = new byte[length];
        ReadRegisters(ado, buffer);
        Array.Copy(buffer, 0, frame, offset, length);
    }

    private void WriteToSlave(ushort ado, byte[] source, int offset, int length)
    {
        var data = new byte[length];
        Array.Copy(source, offset, data, 0, length);
        WriteRegisters(ado, data);
    }

    // ------------------------------------------------------------------ 寄存器空间

    private void ReadRegisters(ushort ado, Span<byte> destination)
    {
        // 按字节逐个读取，支持任意起始地址与长度
        for (int i = 0; i < destination.Length; i++)
        {
            ushort address = (ushort)(ado + i);
            destination[i] = ReadRegisterByte(address);
        }

        // 多字节寄存器需要整体处理，这里覆盖常见读取方式
        if (ado == Esc.AlStatus && destination.Length >= 2)
            BinaryPrimitives.WriteUInt16LittleEndian(destination,
                (ushort)((byte)State | (ErrorIndicator ? 0x10 : 0)));
        if (ado == Esc.AlStatusCode && destination.Length >= 2)
            BinaryPrimitives.WriteUInt16LittleEndian(destination, StatusCode);
        if (ado == Esc.StationAddress && destination.Length >= 2)
            BinaryPrimitives.WriteUInt16LittleEndian(destination, ConfiguredAddress);
        if (ado == Esc.EepStatus && destination.Length >= 2)
            BinaryPrimitives.WriteUInt16LittleEndian(destination, _eepromStatus);
    }

    private byte ReadRegisterByte(ushort address)
    {
        if (address >= Esc.DpramBase)
        {
            int index = address - Esc.DpramBase;
            if (index < _dpram.Length)
            {
                if (IsInRange(address, _sm[1]))
                    OnMailboxRead(address);
                return _dpram[index];
            }
            return 0;
        }

        switch (address)
        {
            case Esc.Type:
                return 0x44;                     // ESC 类型
            case Esc.Type + 1:
                return 0x02;                     // revision
            case 0x0004:                         // FMMU 数量
                return 4;
            case 0x0005:                         // SM 数量
                return 4;
            case 0x0006:                         // RAM size
                return 0x08;
            case 0x0007:                         // port descriptor
                return 0x0F;
            case Esc.StationAddress:
                return (byte)(ConfiguredAddress & 0xFF);
            case Esc.StationAddress + 1:
                return (byte)(ConfiguredAddress >> 8);
            case Esc.Alias:
            case Esc.Alias + 1:
                return 0;
            case Esc.DlStatus:                   // 链路状态：4 个端口均 UP
                return 0x0F;
            case Esc.DlStatus + 1:
                return 0x00;
            case Esc.AlStatus:
                return (byte)State;
            case Esc.AlStatusCode:
                return (byte)(StatusCode & 0xFF);
            case Esc.AlStatusCode + 1:
                return (byte)(StatusCode >> 8);
            case Esc.EepConfig:
                return (byte)(_eepromPdiAccess ? 0x01 : 0x00);
            case Esc.EepStatus:
                return (byte)(_eepromStatus & 0xFF);
            case Esc.EepStatus + 1:
                return (byte)(_eepromStatus >> 8);
            case Esc.EepData:
            case Esc.EepData + 1:
            case Esc.EepData + 2:
            case Esc.EepData + 3:
            case Esc.EepData + 4:
            case Esc.EepData + 5:
            case Esc.EepData + 6:
            case Esc.EepData + 7:
                return ReadEepromDataByte(address - Esc.EepData);
        }

        // FMMU 0x0600 - 0x063F
        if (address >= Esc.Fmmu0 && address < Esc.Fmmu0 + 0x40)
        {
            int n = (address - Esc.Fmmu0) / 0x10;
            int off = (address - Esc.Fmmu0) % 0x10;
            var f = _fmmu[n];
            return off switch
            {
                0 => (byte)(f.LogicalStartAddress & 0xFF),
                1 => (byte)((f.LogicalStartAddress >> 8) & 0xFF),
                2 => (byte)((f.LogicalStartAddress >> 16) & 0xFF),
                3 => (byte)((f.LogicalStartAddress >> 24) & 0xFF),
                4 => (byte)(f.Length & 0xFF),
                5 => (byte)(f.Length >> 8),
                6 => f.LogicalStartBit,
                7 => f.LogicalEndBit,
                8 => (byte)(f.PhysicalStartAddress & 0xFF),
                9 => (byte)(f.PhysicalStartAddress >> 8),
                10 => f.PhysicalStartBit,
                11 => f.Type,
                12 => f.Active,
                _ => (byte)0
            };
        }

        // SM 0x0800 - 0x081F
        if (address >= Esc.Sm0 && address < Esc.Sm0 + 0x20)
        {
            int n = (address - Esc.Sm0) / 0x08;
            int off = (address - Esc.Sm0) % 0x08;
            var s = _sm[n];
            return off switch
            {
                0 => (byte)(s.PhysicalStartAddress & 0xFF),
                1 => (byte)(s.PhysicalStartAddress >> 8),
                2 => (byte)(s.Length & 0xFF),
                3 => (byte)(s.Length >> 8),
                4 => s.Control,
                5 => s.Status,
                6 => s.Activate,
                7 => s.PdiControl,
                _ => (byte)0
            };
        }

        return 0;
    }

    private void WriteRegisters(ushort ado, ReadOnlySpan<byte> data)
    {
        if (ado == Esc.AlControl && data.Length >= 2)
        {
            WriteAlControl(data[0]);
            return;
        }

        if (ado == Esc.StationAddress && data.Length >= 2)
        {
            ConfiguredAddress = BinaryPrimitives.ReadUInt16LittleEndian(data);
            return;
        }

        if (ado == Esc.EepConfig && data.Length >= 1)
        {
            byte value = data[0];
            if (value == 2) _eepromPdiAccess = false;    // 强制取回 EEPROM 控制权
            else if (value == 1) _eepromPdiAccess = true; // 交回 PDI
            else if (value == 0) _eepromPdiAccess = false;
            _eepromConfig = value;
            return;
        }

        if (ado == Esc.EepStatus || ado == Esc.EepControl)
        {
            if (data.Length >= 6)
            {
                ushort comm = BinaryPrimitives.ReadUInt16LittleEndian(data[..2]);
                ushort address = BinaryPrimitives.ReadUInt16LittleEndian(data[2..4]);
                HandleEepromCommand(comm, address);
            }
            else if (data.Length >= 2)
            {
                ushort comm = BinaryPrimitives.ReadUInt16LittleEndian(data[..2]);
                if (comm == Sii.CommandNop)
                    _eepromStatus &= 0x87FF;
            }
            return;
        }

        // FMMU / SM
        if (ado >= Esc.Fmmu0 && ado < Esc.Fmmu0 + 0x40)
        {
            int n = (ado - Esc.Fmmu0) / 0x10;
            int off = (ado - Esc.Fmmu0) % 0x10;
            var f = _fmmu[n];
            for (int i = 0; i < data.Length; i++)
            {
                byte value = data[i];
                switch (off + i)
                {
                    case 0: f.LogicalStartAddress = (f.LogicalStartAddress & 0xFFFFFF00) | value; break;
                    case 1: f.LogicalStartAddress = (f.LogicalStartAddress & 0xFFFF00FF) | ((uint)value << 8); break;
                    case 2: f.LogicalStartAddress = (f.LogicalStartAddress & 0xFF00FFFF) | ((uint)value << 16); break;
                    case 3: f.LogicalStartAddress = (f.LogicalStartAddress & 0x00FFFFFF) | ((uint)value << 24); break;
                    case 4: f.Length = (ushort)((f.Length & 0xFF00) | value); break;
                    case 5: f.Length = (ushort)((f.Length & 0x00FF) | (value << 8)); break;
                    case 6: f.LogicalStartBit = value; break;
                    case 7: f.LogicalEndBit = value; break;
                    case 8: f.PhysicalStartAddress = (ushort)((f.PhysicalStartAddress & 0xFF00) | value); break;
                    case 9: f.PhysicalStartAddress = (ushort)((f.PhysicalStartAddress & 0x00FF) | (value << 8)); break;
                    case 10: f.PhysicalStartBit = value; break;
                    case 11: f.Type = value; break;
                    case 12: f.Active = value; break;
                }
            }
            return;
        }

        if (ado >= Esc.Sm0 && ado < Esc.Sm0 + 0x20)
        {
            int n = (ado - Esc.Sm0) / 0x08;
            int off = (ado - Esc.Sm0) % 0x08;
            var s = _sm[n];
            for (int i = 0; i < data.Length; i++)
            {
                byte value = data[i];
                switch (off + i)
                {
                    case 0: s.PhysicalStartAddress = (ushort)((s.PhysicalStartAddress & 0xFF00) | value); break;
                    case 1: s.PhysicalStartAddress = (ushort)((s.PhysicalStartAddress & 0x00FF) | (value << 8)); break;
                    case 2: s.Length = (ushort)((s.Length & 0xFF00) | value); break;
                    case 3: s.Length = (ushort)((s.Length & 0x00FF) | (value << 8)); break;
                    case 4: s.Control = value; break;
                    case 5: s.Status = value; break;
                    case 6: s.Activate = value; break;
                    case 7: s.PdiControl = value; break;
                }
            }
            return;
        }

        if (ado >= Esc.DpramBase)
        {
            WritePhysical(ado, data.ToArray());
        }
    }

    private void WriteAlControl(byte control)
    {
        bool ackError = (control & 0x10) != 0;
        byte target = (byte)(control & 0x0F);
        if (ackError)
        {
            ErrorIndicator = false;
            StatusCode = 0;
        }

        switch (target)
        {
            case (byte)AlState.Init:
                State = AlState.Init;
                break;
            case (byte)AlState.PreOperational:
                State = AlState.PreOperational;
                break;
            case (byte)AlState.SafeOperational:
                if (!ValidateProcessDataConfiguration())
                {
                    State = AlState.PreOperational;
                    ErrorIndicator = true;
                    StatusCode = 0x0017;   // invalid SM configuration
                }
                else
                {
                    State = AlState.SafeOperational;
                }
                break;
            case (byte)AlState.Operational:
                if (!ValidateProcessDataConfiguration())
                {
                    State = AlState.PreOperational;
                    ErrorIndicator = true;
                    StatusCode = 0x0017;
                }
                else
                {
                    State = AlState.Operational;
                }
                break;
            default:
                StatusCode = 0x0011;   // invalid requested state change
                ErrorIndicator = true;
                break;
        }
    }

    private bool ValidateProcessDataConfiguration()
    {
        bool hasOutput = _sm[2].IsActive || RxPdoDefinitions.Count == 0;
        bool hasInput = _sm[3].IsActive || TxPdoDefinitions.Count == 0;
        return hasOutput && hasInput;
    }

    // ------------------------------------------------------------------ EEPROM

    private void HandleEepromCommand(ushort command, ushort wordAddress)
    {
        switch (command)
        {
            case Sii.CommandRead:
            {
                int byteAddress = wordAddress * 2;
                for (int i = 0; i < 8; i++)
                {
                    int index = byteAddress + i;
                    _eepromData[i] = index < _sii.Length ? _sii[index] : (byte)0xFF;
                }
                _eepromStatus = Sii.StatusRead64;   // 8 字节读取、非忙
                break;
            }
            case Sii.CommandWrite:
                _eepromStatus = 0;
                break;
            case Sii.CommandReload:
                _eepromStatus = 0;
                break;
            default:
                _eepromStatus &= 0x87FF;
                break;
        }
    }

    private readonly byte[] _eepromData = new byte[8];

    private byte ReadEepromDataByte(int offset)
    {
        return offset < _eepromData.Length ? _eepromData[offset] : (byte)0;
    }

    // ------------------------------------------------------------------ 邮箱 / CoE

    private bool IsInRange(ushort address, SyncManagerSetting sm) =>
        sm.IsActive && address >= sm.PhysicalStartAddress && address < sm.PhysicalStartAddress + sm.Length;

    private void OnAfterOutputWrite(int address, int length)
    {
        // 邮箱写入（SM0）
        if (_sm[0].IsActive && address >= _sm[0].PhysicalStartAddress &&
            address < _sm[0].PhysicalStartAddress + _sm[0].Length)
        {
            HandleMailboxRequest();
        }

        // 过程数据输出（SM2）
        if (_sm[2].IsActive && address >= _sm[2].PhysicalStartAddress &&
            address < _sm[2].PhysicalStartAddress + _sm[2].Length)
        {
            OnAfterOutputWrite();
        }
    }

    private void OnMailboxRead(ushort address)
    {
        // 主站读走 SM1 邮箱后清空“满”标志
        if (_sm[1].IsActive && address >= _sm[1].PhysicalStartAddress &&
            address < _sm[1].PhysicalStartAddress + _sm[1].Length)
        {
            _sm[1].Status &= 0xF7;
        }
    }

    private void HandleMailboxRequest()
    {
        int start = _sm[0].PhysicalStartAddress - Esc.DpramBase;
        int size = Math.Min(_sm[0].Length, _dpram.Length - start);
        if (start < 0 || size <= Mailbox.HeaderSize)
            return;

        var request = new byte[size];
        Array.Copy(_dpram, start, request, 0, size);

        ushort length = BinaryPrimitives.ReadUInt16LittleEndian(request.AsSpan(0, 2));
        if (length == 0 || length + Mailbox.HeaderSize > size)
            return;
        if (Mailbox.GetType(request[5]) != Mailbox.Coe)
            return;

        int payloadLength = length;
        var response = BuildCoEResponse(request.AsSpan(Mailbox.HeaderSize, payloadLength));
        if (response == null)
            return;

        int outStart = _sm[1].PhysicalStartAddress - Esc.DpramBase;
        if (outStart < 0 || outStart + Mailbox.HeaderSize + response.Length > _dpram.Length)
            return;

        var mailbox = new byte[Mailbox.HeaderSize + response.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(mailbox.AsSpan(0, 2), (ushort)response.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(mailbox.AsSpan(2, 2), 0);
        mailbox[4] = 0;
        mailbox[5] = Mailbox.BuildTypeField(Mailbox.Coe, _mailboxCounter);
        response.CopyTo(mailbox, Mailbox.HeaderSize);
        Array.Copy(mailbox, 0, _dpram, outStart, mailbox.Length);

        _sm[1].Status |= 0x08;   // 邮箱满
        _mailboxCounter = (byte)((_mailboxCounter + 1) & 0x0F);
    }

    private byte[]? BuildCoEResponse(ReadOnlySpan<byte> coe)
    {
        if (coe.Length < 3)
            return null;

        ushort header = BinaryPrimitives.ReadUInt16LittleEndian(coe[..2]);
        int service = Coe.GetService(header);
        byte command = coe[2];

        switch (service)
        {
            case Coe.SdoRequest:
                return BuildSdoResponse(header, command, coe);
            default:
                return null;
        }
    }

    private byte[]? BuildSdoResponse(ushort requestHeader, byte command, ReadOnlySpan<byte> coe)
    {
        // 快速（expedited）上传
        if (command == Coe.UploadInitiate)
        {
            ushort index = BinaryPrimitives.ReadUInt16LittleEndian(coe.Slice(3, 2));
            byte subIndex = coe[5];
            if (!TryGetObject(index, subIndex, out var value))
                return BuildAbort(requestHeader, index, subIndex, 0x06020000);

            var response = new byte[Coe.HeaderSize + 1 + 2 + 1 + 4];
            BinaryPrimitives.WriteUInt16LittleEndian(response.AsSpan(0, 2),
                Coe.BuildHeader(Coe.GetNumber(requestHeader), Coe.SdoResponse));
            response[2] = (byte)(0x43 | ((4 - value.Length) << 2));
            BinaryPrimitives.WriteUInt16LittleEndian(response.AsSpan(3, 2), index);
            response[5] = subIndex;
            for (int i = 0; i < 4; i++)
                response[6 + i] = i < value.Length ? value[i] : (byte)0;
            return response;
        }

        // 快速下载：bit7-5 = 001 且 bit1 = expedited（长度由 bit0+bit2-3 指示）
        bool isDownloadRequest = (command & 0xE0) == 0x20;
        bool isExpedited = (command & 0x02) != 0;
        if (isDownloadRequest && (isExpedited || command == Coe.DownloadInitiate))
        {
            ushort index = BinaryPrimitives.ReadUInt16LittleEndian(coe.Slice(3, 2));
            byte subIndex = coe[5];
            int dataLength = isExpedited
                ? ((command & 0x01) != 0 ? 4 - ((command >> 2) & 0x03) : 4)
                : coe.Length - 6;
            if (dataLength < 0) dataLength = 0;
            if (!TryGetObject(index, subIndex, out var existing))
                return BuildAbort(requestHeader, index, subIndex, 0x06020000);
            if (dataLength > existing.Length)
                return BuildAbort(requestHeader, index, subIndex, 0x06070012);

            var data = new byte[existing.Length];
            for (int i = 0; i < dataLength && i < existing.Length; i++)
                data[i] = coe[6 + i];
            SetObject(index, subIndex, data);

            var response = new byte[Coe.HeaderSize + 1 + 2 + 1 + 4];
            BinaryPrimitives.WriteUInt16LittleEndian(response.AsSpan(0, 2),
                Coe.BuildHeader(Coe.GetNumber(requestHeader), Coe.SdoResponse));
            response[2] = 0x60;
            BinaryPrimitives.WriteUInt16LittleEndian(response.AsSpan(3, 2), index);
            response[5] = subIndex;
            return response;
        }

        return null;
    }

    private byte[] BuildAbort(ushort requestHeader, ushort index, byte subIndex, uint abortCode)
    {
        var response = new byte[Coe.HeaderSize + 1 + 2 + 1 + 4];
        BinaryPrimitives.WriteUInt16LittleEndian(response.AsSpan(0, 2),
            Coe.BuildHeader(Coe.GetNumber(requestHeader), Coe.SdoResponse));
        response[2] = Coe.Abort;
        BinaryPrimitives.WriteUInt16LittleEndian(response.AsSpan(3, 2), index);
        response[5] = subIndex;
        BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(6, 4), abortCode);
        return response;
    }

    // ------------------------------------------------------------------ 行为模型

    private void OnAfterOutputWrite()
    {
        MapOutputsToObjects();

        if (Behavior != null)
            Behavior.OnOutputsWritten(this, _lastDelta);
        else
        {
            UpdateDriveModel(0);
            UpdateIoOutputs();
        }
    }

    private void MapOutputsToObjects()
    {
        // 把 SM2 中的输出数据按 PDO 映射写入对象字典
        int bitOffset = 0;
        int start = _sm[2].PhysicalStartAddress - Esc.DpramBase;
        if (start < 0)
            return;
        var buffer = _dpram.AsSpan(start, Math.Min(_sm[2].Length, _dpram.Length - start));

        foreach (var pdo in RxPdoDefinitions)
        {
            foreach (var entry in pdo.Entries)
            {
                int bitLength = entry.BitLength > 0 ? entry.BitLength : 16;
                ulong value = PdoBitAccess.ReadBits(buffer, bitOffset, bitLength);
                int size = (bitLength + 7) / 8;
                SetObjectValue(entry.Index, entry.SubIndex, value, size);
                bitOffset += bitLength;
            }
        }
    }

    private void OnBeforeInputRead()
    {
        // 按 PDO 映射把对象字典内容写入 SM3
        int start = _sm[3].PhysicalStartAddress - Esc.DpramBase;
        if (start < 0)
            return;

        if (Behavior != null)
            Behavior.BeforeInputsRead(this, _lastDelta);
        else
            UpdateDigitalInputs();

        var buffer = _dpram.AsSpan(start, Math.Min(_sm[3].Length, _dpram.Length - start));
        int bitOffset = 0;
        foreach (var pdo in TxPdoDefinitions)
        {
            foreach (var entry in pdo.Entries)
            {
                int bitLength = entry.BitLength > 0 ? entry.BitLength : 16;
                ulong value = GetObjectValue(entry.Index, entry.SubIndex);
                PdoBitAccess.WriteBits(buffer, bitOffset, bitLength, value);
                bitOffset += bitLength;
            }
        }
    }

    private void UpdateDriveModel(double dtSeconds)
    {
        if (!TryGetObject(0x6040, 0, out _) || !TryGetObject(0x6041, 0, out _))
            return;   // 非伺服类从站

        ushort controlword = (ushort)GetObjectValue(0x6040, 0);
        ushort statusword = (ushort)GetObjectValue(0x6041, 0);

        bool fault = (statusword & 0x0008) != 0;
        if ((controlword & 0x0080) != 0)   // fault reset
        {
            fault = false;
            statusword &= 0xFFF7;
            SetObjectValue(0x603F, 0, 0, 2);
        }

        if (!fault)
        {
            switch (controlword & 0x000F)
            {
                case 0x06:   // shutdown
                    statusword = 0x0231;
                    break;
                case 0x07:   // switch on
                    statusword = 0x0233;
                    break;
                case 0x0F:   // enable operation
                    statusword = 0x1237;
                    break;
                case 0x00:   // switch on disabled
                    statusword = 0x0240;
                    break;
                case 0x02:   // quick stop
                    statusword = 0x0217;
                    break;
            }
            if ((controlword & 0x000F) == 0x0F)
                statusword |= 0x0400;   // target reached
        }
        else
        {
            statusword = 0x0218;
        }

        SetObjectValue(0x6041, 0, statusword, 2);
        SetObjectValue(0x6061, 0, GetObjectValue(0x6060, 0), 1);
    }

    private void UpdateIoOutputs()
    {
        if (TryGetObject(0x7000, 1, out _))
            DigitalOutputs = (ushort)GetObjectValue(0x7000, 1);
    }

    private void UpdateDigitalInputs()
    {
        if (TryGetObject(0x6000, 1, out _))
            SetObjectValue(0x6000, 1, DigitalInputs, 2);
    }

    /// <summary>
    /// 周期性推进仿真模型（伺服位置积分等）。由仿真链路在每个周期调用。
    /// </summary>
    public void Simulate(double dtSeconds)
    {
        _lastDelta = dtSeconds > 0 ? dtSeconds : 0.001;

        if (Behavior != null)
        {
            Behavior.Simulate(this, _lastDelta);
            return;
        }

        SimulateLegacy(_lastDelta);
    }

    /// <summary>内置的简易伺服模型（未挂接行为模型时使用）</summary>
    private void SimulateLegacy(double dtSeconds)
    {
        ushort statusword = (ushort)GetObjectValue(0x6041, 0);
        bool enabled = (statusword & 0x0004) != 0 && (statusword & 0x0008) == 0;

        if (enabled)
        {
            byte mode = (byte)GetObjectValue(0x6061, 0);
            long targetPosition = unchecked((int)(uint)GetObjectValue(0x607A, 0));
            int targetVelocity = unchecked((int)(uint)GetObjectValue(0x60FF, 0));

            switch (mode)
            {
                case 3:   // profile velocity
                    ActualVelocityInternal = targetVelocity;
                    ActualPositionInternal += (long)(targetVelocity * dtSeconds);
                    break;
                case 1:   // profile position
                case 6:   // homing
                {
                    long error = targetPosition - ActualPositionInternal;
                    int maxStep = Math.Max(1, Math.Abs(targetVelocity == 0 ? 200000 : targetVelocity));
                    int step = (int)Math.Max(-maxStep, Math.Min(maxStep, error));
                    ActualVelocityInternal = (int)(step / Math.Max(dtSeconds, 1e-6));
                    ActualPositionInternal += step;
                    if (mode == 6)
                    {
                        statusword |= 0x1000;   // homing attained
                        SetObjectValue(0x6041, 0, statusword, 2);
                    }
                    break;
                }
                case 8:   // cyclic synchronous position
                    ActualVelocityInternal = (int)((targetPosition - ActualPositionInternal) / Math.Max(dtSeconds, 1e-6));
                    ActualPositionInternal = targetPosition;
                    break;
                case 9:   // cyclic synchronous velocity
                    ActualVelocityInternal = targetVelocity;
                    ActualPositionInternal += (long)(targetVelocity * dtSeconds);
                    break;
                default:
                    ActualVelocityInternal = 0;
                    break;
            }
        }
        else
        {
            ActualVelocityInternal = 0;
        }

        SetObjectValue(0x6064, 0, unchecked((ulong)(long)ActualPositionInternal), 4);
        SetObjectValue(0x606C, 0, unchecked((ulong)ActualVelocityInternal), 4);
    }
}

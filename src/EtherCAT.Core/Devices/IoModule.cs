using System;
using EtherCAT.Master;

namespace EtherCAT.Devices;

/// <summary>
/// 数字量 IO 模块的单个通道
/// </summary>
public sealed class IoChannel
{
    private readonly PdoVariable _variable;
    private readonly int _bitIndex;

    internal IoChannel(PdoVariable variable, int bitIndex, string name, bool isOutput)
    {
        _variable = variable;
        _bitIndex = bitIndex;
        Name = name;
        IsOutput = isOutput;
    }

    public string Name { get; }
    public bool IsOutput { get; }

    public bool Value
    {
        get
        {
            // 位级 PDO（BitLength == 1）直接取值，否则按位掩码取
            return _variable.BitLength == 1
                ? _variable.RawValue != 0
                : (_variable.RawValue & (1UL << _bitIndex)) != 0;
        }
        set
        {
            if (_variable.BitLength == 1)
            {
                _variable.RawValue = value ? 1UL : 0UL;
            }
            else
            {
                ulong raw = _variable.RawValue;
                if (value)
                    raw |= 1UL << _bitIndex;
                else
                    raw &= ~(1UL << _bitIndex);
                _variable.RawValue = raw;
            }
        }
    }

    public override string ToString() => $"{Name} = {(Value ? 1 : 0)}";
}

/// <summary>
/// 模拟量通道（0x6401 输入 / 0x6411 输出，INT16，默认 ±10 V 量程）
/// </summary>
public sealed class AnalogChannel
{
    private readonly PdoVariable _variable;

    internal AnalogChannel(PdoVariable variable, string name, bool isOutput)
    {
        _variable = variable;
        Name = name;
        IsOutput = isOutput;
    }

    public string Name { get; }
    public bool IsOutput { get; }
    public PdoVariable Variable => _variable;

    /// <summary>满量程电压（对应原始值 32767）</summary>
    public double RangeVolts { get; set; } = 10.0;

    /// <summary>原始 ADC/DAC 值（有符号 16 位）</summary>
    public int Raw
    {
        get => unchecked((short)(ushort)_variable.RawValue);
        set => _variable.RawValue = unchecked((ushort)(short)value);
    }

    /// <summary>工程量（V）</summary>
    public double Volts
    {
        get => Raw / 32767.0 * RangeVolts;
        set => Raw = (int)Math.Round(Math.Clamp(value, -RangeVolts, RangeVolts) / RangeVolts * 32767);
    }

    public override string ToString() => $"{Name} = {Volts:F3} V (raw {Raw})";
}

/// <summary>
/// IO 模块：自动识别 0x6000/0x7000 系列数字量对象与 0x6401/0x6411 模拟量对象
/// </summary>
public sealed class IoModule
{
    public SlaveInfo Slave { get; }
    public int SlaveIndex => Slave.SlaveIndex;

    public List<IoChannel> Inputs { get; } = new();
    public List<IoChannel> Outputs { get; } = new();
    public List<AnalogChannel> AnalogInputs { get; } = new();
    public List<AnalogChannel> AnalogOutputs { get; } = new();

    public bool HasAnalog => AnalogInputs.Count > 0 || AnalogOutputs.Count > 0;

    public IoModule(SlaveInfo slave)
    {
        Slave = slave;

        foreach (var variable in slave.Inputs)
        {
            if (IsAnalogInputIndex(variable.Index))
                AnalogInputs.Add(new AnalogChannel(variable,
                    string.IsNullOrWhiteSpace(variable.Name) ? $"AI {AnalogInputs.Count}" : variable.Name, false));
            else if (IsDigitalInputIndex(variable.Index))
                AddChannels(variable, false);
        }

        foreach (var variable in slave.Outputs)
        {
            if (IsAnalogOutputIndex(variable.Index))
                AnalogOutputs.Add(new AnalogChannel(variable,
                    string.IsNullOrWhiteSpace(variable.Name) ? $"AO {AnalogOutputs.Count}" : variable.Name, true));
            else if (IsDigitalOutputIndex(variable.Index))
                AddChannels(variable, true);
        }
    }

    private static bool IsAnalogInputIndex(ushort index) =>
        index is 0x6401 or 0x6402 || (index >= 0x6420 && index <= 0x643F);

    private static bool IsAnalogOutputIndex(ushort index) =>
        index is 0x6411 or 0x6412 || (index >= 0x6440 && index <= 0x645F);

    private static bool IsDigitalInputIndex(ushort index) =>
        (index & 0xF000) == 0x6000 && !IsAnalogInputIndex(index);

    private static bool IsDigitalOutputIndex(ushort index) =>
        (index & 0xF000) == 0x7000 && !IsAnalogOutputIndex(index);

    private void AddChannels(PdoVariable variable, bool isOutput)
    {
        var target = isOutput ? Outputs : Inputs;

        if (variable.BitLength == 1)
        {
            string name = isOutput
                ? $"DO {target.Count}"
                : $"DI {target.Count}";
            if (!string.IsNullOrWhiteSpace(variable.Name))
                name = variable.Name;
            target.Add(new IoChannel(variable, 0, name, isOutput));
            return;
        }

        int bitCount = Math.Min(variable.BitLength, 32);
        for (int bit = 0; bit < bitCount; bit++)
        {
            string name = isOutput ? $"DO {target.Count}" : $"DI {target.Count}";
            target.Add(new IoChannel(variable, bit, name, isOutput));
        }
    }

    /// <summary>整体输入字（把各通道按顺序压缩成一个整数）</summary>
    public ulong InputWord
    {
        get
        {
            ulong value = 0;
            for (int i = 0; i < Inputs.Count && i < 64; i++)
                if (Inputs[i].Value)
                    value |= 1UL << i;
            return value;
        }
    }

    public ulong OutputWord
    {
        get
        {
            ulong value = 0;
            for (int i = 0; i < Outputs.Count && i < 64; i++)
                if (Outputs[i].Value)
                    value |= 1UL << i;
            return value;
        }
        set
        {
            for (int i = 0; i < Outputs.Count && i < 64; i++)
                Outputs[i].Value = (value & (1UL << i)) != 0;
        }
    }

    public void SetOutput(int channel, bool value)
    {
        if (channel >= 0 && channel < Outputs.Count)
            Outputs[channel].Value = value;
    }

    public bool GetInput(int channel) =>
        channel >= 0 && channel < Inputs.Count && Inputs[channel].Value;

    public override string ToString()
    {
        string analog = HasAnalog ? $"，{AnalogInputs.Count} AI / {AnalogOutputs.Count} AO" : "";
        return $"IO #{SlaveIndex} {Slave.ProductName}（{Inputs.Count} DI / {Outputs.Count} DO{analog}）";
    }
}

/// <summary>
/// 设备识别工厂：从扫描结果中创建驱动器与 IO 模块
/// </summary>
public static class DeviceFactory
{
    /// <summary>多轴驱动器的轴索引间隔（CiA402 约定：第 2 轴 +0x800）</summary>
    public const int AxisIndexStep = 0x800;

    public static List<CiA402Drive> FindDrives(EthercatMaster master)
    {
        var result = new List<CiA402Drive>();

        foreach (var slave in master.Slaves.Where(s => s.IsDrive))
        {
            var indices = new HashSet<ushort>();
            foreach (var variable in slave.Outputs.Concat(slave.Inputs))
                indices.Add(variable.Index);
            foreach (var pdo in slave.Esi?.RxPdos.Concat(slave.Esi.TxPdos) ?? Enumerable.Empty<Esi.EsiPdo>())
                foreach (var entry in pdo.Entries)
                    indices.Add(entry.Index);

            bool any = false;
            for (int axis = 1; axis <= 8; axis++)
            {
                ushort controlword = (ushort)(CiA402Drive.IndexControlword + (axis - 1) * AxisIndexStep);
                ushort statusword = (ushort)(CiA402Drive.IndexStatusword + (axis - 1) * AxisIndexStep);
                if (!indices.Contains(controlword) || !indices.Contains(statusword))
                    continue;
                result.Add(new CiA402Drive(master, slave, axis));
                any = true;
            }

            // PDO 里没有控制字/状态字时仍然创建一个轴对象，之后走 SDO 通道
            if (!any)
                result.Add(new CiA402Drive(master, slave, 1));
        }

        return result;
    }

    public static List<IoModule> FindIoModules(EthercatMaster master) =>
        master.Slaves
            .Where(s => s.IsIoModule)
            .Select(s => new IoModule(s))
            .ToList();

    /// <summary>发现网络中的 ACS 驱动器（标准 CiA402 伺服），每个轴封装为一个 <see cref="AcsDrive"/></summary>
    public static List<AcsDrive> FindAcsDrives(EthercatMaster master, Action<string>? log = null)
    {
        var result = new List<AcsDrive>();
        foreach (var drive in FindDrives(master))
            result.Add(new AcsDrive(drive, log));
        return result;
    }
}

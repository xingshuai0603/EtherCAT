using EtherCAT.Master;

namespace EtherCAT.Devices;

/// <summary>CiA402 运行模式</summary>
public enum CiA402Mode : sbyte
{
    NoMode = 0,
    ProfilePosition = 1,
    Velocity = 2,
    ProfileVelocity = 3,
    TorqueProfile = 4,
    Homing = 6,
    InterpolatedPosition = 7,
    CyclicSynchronousPosition = 8,
    CyclicSynchronousVelocity = 9,
    CyclicSynchronousTorque = 10
}

/// <summary>
/// CiA402 伺服驱动器：封装状态机（0x6040/0x6041）与常用运行模式
/// </summary>
public sealed class CiA402Drive
{
    // CiA402 对象字典索引
    public const ushort IndexControlword = 0x6040;
    public const ushort IndexStatusword = 0x6041;
    public const ushort IndexModesOfOperation = 0x6060;
    public const ushort IndexModesOfOperationDisplay = 0x6061;
    public const ushort IndexPositionActual = 0x6064;
    public const ushort IndexVelocityActual = 0x606C;
    public const ushort IndexTorqueActual = 0x6077;
    public const ushort IndexTargetPosition = 0x607A;
    public const ushort IndexTargetVelocity = 0x60FF;
    public const ushort IndexTargetTorque = 0x6071;
    public const ushort IndexErrorCode = 0x603F;
    public const ushort IndexProfileVelocity = 0x6081;
    public const ushort IndexProfileAcceleration = 0x6083;
    public const ushort IndexProfileDeceleration = 0x6084;
    public const ushort IndexHomingMethod = 0x6098;

    private readonly EthercatMaster? _master;
    private readonly int _axisOffset;

    public SlaveInfo Slave { get; }
    public int SlaveIndex => Slave.SlaveIndex;

    /// <summary>轴号（1-based）。多轴驱动器第 n 轴的对象索引 = 基准索引 + (n-1)×0x800</summary>
    public int AxisNumber { get; }

    /// <summary>把基准索引换算到本轴的实际索引</summary>
    private ushort Idx(ushort index) => (ushort)(index + _axisOffset);

    /// <summary>显示名（多轴时带轴号）</summary>
    public string DisplayName => AxisNumber > 1 ? $"轴{AxisNumber} {Slave.ProductName}" : Slave.ProductName;

    private readonly PdoVariable? _controlword;
    private readonly PdoVariable? _statusword;
    private readonly PdoVariable? _targetPosition;
    private readonly PdoVariable? _targetVelocity;
    private readonly PdoVariable? _targetTorque;
    private readonly PdoVariable? _mode;
    private readonly PdoVariable? _modeDisplay;
    private readonly PdoVariable? _positionActual;
    private readonly PdoVariable? _velocityActual;
    private readonly PdoVariable? _torqueActual;
    private readonly PdoVariable? _errorCode;

    /// <summary>控制字/状态字是否在 PDO 中（否则需要 SDO 访问）</summary>
    public bool UsesPdo => _controlword != null && _statusword != null;

    public CiA402Drive(EthercatMaster master, SlaveInfo slave, int axisNumber = 1)
    {
        _master = master;
        Slave = slave;
        AxisNumber = axisNumber < 1 ? 1 : axisNumber;
        _axisOffset = (AxisNumber - 1) * 0x800;

        _controlword = FindOutput(Idx(IndexControlword));
        _statusword = FindInput(Idx(IndexStatusword));
        _targetPosition = FindOutput(Idx(IndexTargetPosition));
        _targetVelocity = FindOutput(Idx(IndexTargetVelocity));
        _targetTorque = FindOutput(Idx(IndexTargetTorque));
        _mode = FindOutput(Idx(IndexModesOfOperation));
        _modeDisplay = FindInput(Idx(IndexModesOfOperationDisplay));
        _positionActual = FindInput(Idx(IndexPositionActual));
        _velocityActual = FindInput(Idx(IndexVelocityActual));
        _torqueActual = FindInput(Idx(IndexTorqueActual));
        _errorCode = FindInput(Idx(IndexErrorCode));
    }

    private PdoVariable? FindOutput(ushort index) =>
        Slave.Outputs.FirstOrDefault(v => v.Index == index);

    private PdoVariable? FindInput(ushort index) =>
        Slave.Inputs.FirstOrDefault(v => v.Index == index);

    // ------------------------------------------------------------------ 状态

    public ushort Statusword => _statusword?.RawValue is { } v ? (ushort)v : ReadSdo16(Idx(IndexStatusword));

    public bool ReadyToSwitchOn => (Statusword & 0x0001) != 0;
    public bool SwitchedOn => (Statusword & 0x0002) != 0;
    public bool OperationEnabled => (Statusword & 0x0004) != 0;
    public bool Fault => (Statusword & 0x0008) != 0;
    public bool VoltageEnabled => (Statusword & 0x0010) != 0;
    public bool QuickStopActive => (Statusword & 0x0020) == 0;
    public bool SwitchOnDisabled => (Statusword & 0x0040) != 0;
    public bool Warning => (Statusword & 0x0080) != 0;
    public bool TargetReached => (Statusword & 0x0400) != 0;
    /// <summary>回零完成（statusword bit12，HM 模式下为 homing attained）</summary>
    public bool HomingAttained => (Statusword & 0x1000) != 0;
    /// <summary>跟随误差报警（statusword bit13）</summary>
    public bool FollowingError => (Statusword & 0x2000) != 0;

    public string StateText =>
        Fault ? "Fault" :
        OperationEnabled ? "Operation Enabled" :
        SwitchedOn ? "Switched On" :
        ReadyToSwitchOn ? "Ready to Switch On" :
        SwitchOnDisabled ? "Switch On Disabled" : "Unknown";

    public int PositionActual => _positionActual?.SignedValue ?? ReadSdo32Signed(Idx(IndexPositionActual));
    public int VelocityActual => _velocityActual?.SignedValue ?? ReadSdo32Signed(Idx(IndexVelocityActual));
    public short TorqueActual => (short)(_torqueActual?.RawValue ?? ReadSdo16(Idx(IndexTorqueActual)));
    public ushort ErrorCode => _errorCode?.RawValue is { } v ? (ushort)v : ReadSdo16(Idx(IndexErrorCode));

    public CiA402Mode Mode
    {
        get => (CiA402Mode)(sbyte)(_modeDisplay?.RawValue ?? ReadSdo16(Idx(IndexModesOfOperationDisplay)));
        set
        {
            if (_mode != null)
                _mode.RawValue = (byte)(sbyte)value;
            else
                WriteSdo(Idx(IndexModesOfOperation), new[] { (byte)(sbyte)value });
        }
    }

    // ------------------------------------------------------------------ 控制

    public ushort Controlword
    {
        get => (ushort)(_controlword?.RawValue ?? 0);
        set
        {
            if (_controlword != null)
                _controlword.RawValue = value;
        }
    }

    /// <summary>
    /// 按 CiA402 状态机使能伺服。采用"持续驱动"的写法：每次轮询都根据当前状态字
    /// 重新下发控制字，因此对瞬时读到旧值/中间值、或从站需要重复触发的情况都能收敛。
    /// </summary>
    public bool Enable(int timeoutMs = 3000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            PumpProcessData();

            if (Fault)
            {
                FaultResetInternal();
                continue;
            }

            if (OperationEnabled)
                return true;

            if (!ReadyToSwitchOn)
                Controlword = 0x0006;      // Shutdown
            else if (!SwitchedOn)
                Controlword = 0x0007;      // Switch on
            else
                Controlword = 0x000F;      // Enable operation

            Thread.Sleep(10);
        }

        return OperationEnabled;
    }

    public void Disable()
    {
        Controlword = 0x0000;
    }

    public void QuickStop()
    {
        Controlword = 0x0002;
    }

    public void FaultReset() => FaultResetInternal();

    private void FaultResetInternal()
    {
        Controlword = 0x0000;
        PumpProcessData();
        Thread.Sleep(20);
        Controlword = 0x0080;   // fault reset
        PumpProcessData();
        Thread.Sleep(20);
        Controlword = 0x0000;
        PumpProcessData();
    }

    /// <summary>周期任务未运行时手动推进一次过程数据交换</summary>
    private void PumpProcessData()
    {
        if (_master is { IsRunning: false, IsConfigured: true })
            _master.SendProcessData();
    }

    public void Halt(bool halt = true)
    {
        ushort cw = Controlword;
        if (halt)
            Controlword = (ushort)(cw | 0x0100);
        else
            Controlword = (ushort)(cw & ~0x0100);
    }

    /// <summary>启动回零</summary>
    public void StartHoming(byte method = 17)
    {
        Mode = CiA402Mode.Homing;
        WriteSdo(Idx(IndexHomingMethod), new[] { method });
        Controlword = (ushort)(Controlword | 0x0010);   // 启动回零
    }

    public void SetTargetPosition(int position)
    {
        if (_targetPosition != null)
            _targetPosition.SignedValue = position;
        else
            WriteSdo(Idx(IndexTargetPosition), BitConverter.GetBytes(position));
    }

    public void SetTargetVelocity(int velocity)
    {
        if (_targetVelocity != null)
            _targetVelocity.SignedValue = velocity;
        else
            WriteSdo(Idx(IndexTargetVelocity), BitConverter.GetBytes(velocity));
    }

    public void SetTargetTorque(short torque)
    {
        if (_targetTorque != null)
            _targetTorque.RawValue = (ushort)torque;
        else
            WriteSdo(Idx(IndexTargetTorque), BitConverter.GetBytes(torque));
    }

    public void SetProfileVelocity(uint velocity) => WriteSdo(Idx(IndexProfileVelocity), BitConverter.GetBytes(velocity));
    public void SetProfileAcceleration(uint acceleration) => WriteSdo(Idx(IndexProfileAcceleration), BitConverter.GetBytes(acceleration));
    public void SetProfileDeceleration(uint deceleration) => WriteSdo(Idx(IndexProfileDeceleration), BitConverter.GetBytes(deceleration));

    public int TargetPosition => _targetPosition?.SignedValue ?? 0;
    public int TargetVelocity => _targetVelocity?.SignedValue ?? 0;

    private bool WaitFor(Func<CiA402Drive, bool> predicate, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            PumpProcessData();

            if (predicate(this))
                return true;
            Thread.Sleep(5);
        }
        return predicate(this);
    }

    // ------------------------------------------------------------------ SDO 兜底

    private ushort ReadSdo16(ushort index)
    {
        // 从站尚未完成配置（处于 INIT/PREOP 或扫描刚完成时）时不主动走 SDO，
        // 否则周期刷新定时器每 100ms 会对未就绪的从站发起邮箱访问，刷屏且浪费。
        if (_master == null || !_master.IsConfigured) return 0;
        try
        {
            return _master.Coe.ReadUInt16(SlaveIndex, index, 0);
        }
        catch
        {
            return 0;
        }
    }

    private int ReadSdo32Signed(ushort index)
    {
        if (_master == null || !_master.IsConfigured) return 0;
        try
        {
            return _master.Coe.ReadInt32(SlaveIndex, index, 0);
        }
        catch
        {
            return 0;
        }
    }

    private void WriteSdo(ushort index, byte[] data)
    {
        if (_master == null) return;
        try
        {
            _master.Coe.Download(SlaveIndex, index, 0, data);
        }
        catch (Exception ex)
        {
            throw new EthercatException($"从站 #{SlaveIndex} SDO 写 0x{index:X4} 失败：{ex.Message}", ex);
        }
    }

    public override string ToString() => $"CiA402 #{SlaveIndex} {DisplayName}";
}

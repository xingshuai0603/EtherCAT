using System.Globalization;
using System.Text;

namespace EtherCAT.Link.Simulated;

/// <summary>
/// 虚拟伺服驱动器的可调参数（机械与电气特性）
/// </summary>
public sealed class VirtualDriveOptions
{
    /// <summary>轴数（1 = 单轴；2 = 双轴，第二轴对象索引 +0x800）</summary>
    public int AxisCount { get; set; } = 1;

    /// <summary>编码器分辨率（counts/转）</summary>
    public int EncoderResolution { get; set; } = 10000;

    /// <summary>最大速度（counts/s）</summary>
    public double MaxVelocity { get; set; } = 1_000_000;

    /// <summary>最大加速度（counts/s^2）</summary>
    public double MaxAcceleration { get; set; } = 4_000_000;

    /// <summary>位置环增益（1/s），决定 CSP/PP 的跟随滞后</summary>
    public double PositionLoopGain { get; set; } = 200;

    /// <summary>等效惯量（用于转矩估算与 CST 模式）</summary>
    public double Inertia { get; set; } = 1.0;

    /// <summary>粘性摩擦系数</summary>
    public double ViscousFriction { get; set; } = 0.02;

    /// <summary>恒定负载转矩（重力、摩擦等，额定转矩的比值）</summary>
    public double LoadTorque { get; set; } = 0.0;

    /// <summary>正/负软限位（counts）</summary>
    public int PositiveLimit { get; set; } = 2_000_000;
    public int NegativeLimit { get; set; } = -2_000_000;

    /// <summary>跟随误差窗口（counts），0 = 不检查</summary>
    public int FollowingErrorWindow { get; set; } = 0;

    /// <summary>到位窗口（counts）</summary>
    public int PositionWindow { get; set; } = 200;

    /// <summary>原点开关有效窗口（counts）：位置进入该窗口即认为压到原点开关</summary>
    public int HomeSwitchWindow { get; set; } = 1000;

    /// <summary>回零超时（秒），超时报 0x8612</summary>
    public double HomingTimeout { get; set; } = 10.0;

    public VirtualDriveOptions Clone() => (VirtualDriveOptions)MemberwiseClone();
}

/// <summary>单轴的仿真运行状态</summary>
public sealed class VirtualDriveAxis
{
    public int AxisNumber { get; init; }

    /// <summary>实际位置（counts）</summary>
    public double Position { get; set; }
    /// <summary>实际速度（counts/s）</summary>
    public double Velocity { get; set; }
    /// <summary>指令位置（规划器输出）</summary>
    public double CommandPosition { get; set; }
    /// <summary>实际转矩（额定转矩的比值，-1..1）</summary>
    public double Torque { get; set; }

    public ushort ErrorCode { get; set; }
    public bool Fault { get; set; }
    public bool TargetReached { get; set; }
    public bool HomingAttained { get; set; }
    public bool HomingActive { get; set; }
    public double HomingElapsed { get; set; }
    public bool Warning { get; set; }

    /// <summary>传感器实际状态（每周期按位置自动判定，可被 Override 强制）</summary>
    public bool HomeSwitch { get; set; }
    public bool PositiveLimitSwitch { get; set; }
    public bool NegativeLimitSwitch { get; set; }
    public bool ProbeInput { get; set; }

    /// <summary>外部强制原点开关（null = 按位置自动判定）</summary>
    public bool? HomeSwitchOverride { get; set; }
    /// <summary>外部强制正限位（null = 按位置自动判定）</summary>
    public bool? PositiveLimitOverride { get; set; }
    /// <summary>外部强制负限位（null = 按位置自动判定）</summary>
    public bool? NegativeLimitOverride { get; set; }

    public bool ProbeCaptured { get; set; }
    public int ProbeCapturedPosition { get; set; }
    public bool ProbeArmed { get; set; }
    public bool ProbeLastInput { get; set; }

    public CiA402SubState SubState { get; set; } = CiA402SubState.SwitchOnDisabled;
    public byte Mode { get; set; }
    public bool LastNewSetPoint { get; set; }
}

/// <summary>CiA402 状态机子状态</summary>
public enum CiA402SubState
{
    SwitchOnDisabled,
    ReadyToSwitchOn,
    SwitchedOn,
    OperationEnabled,
    QuickStopActive,
    FaultReactionActive,
    Fault
}

/// <summary>
/// 虚拟 CiA402 伺服：实现完整状态机、PP/PV/HM/CSP/CSV/CST 运动学、
/// 软限位、跟随误差、回零、探针捕获与故障注入。
/// </summary>
public sealed class VirtualDriveBehavior : ISlaveBehavior
{
    private readonly VirtualDriveOptions _options;
    private readonly List<VirtualDriveAxis> _axes = new();

    public VirtualDriveBehavior(VirtualDriveOptions? options = null)
    {
        _options = options ?? new VirtualDriveOptions();
        for (int i = 0; i < Math.Max(1, _options.AxisCount); i++)
            _axes.Add(new VirtualDriveAxis { AxisNumber = i + 1 });
    }

    public VirtualDriveOptions Options => _options;
    public IReadOnlyList<VirtualDriveAxis> Axes => _axes;

    public VirtualDriveAxis Axis(int zeroBasedIndex) => _axes[zeroBasedIndex];

    /// <summary>注入故障（用于测试主站的故障处理与故障复位流程）</summary>
    public void TriggerFault(int zeroBasedAxis, ushort errorCode = 0x6300)
    {
        var axis = _axes[zeroBasedAxis];
        axis.Fault = true;
        axis.ErrorCode = errorCode;
        axis.SubState = CiA402SubState.Fault;
        axis.Velocity = 0;
        axis.HomingActive = false;
    }

    // ------------------------------------------------------------ 索引换算

    /// <summary>轴编号 → 对象索引偏移（第 2 轴 +0x800，符合 CiA402 多轴约定）</summary>
    public static int AxisOffset(int axisNumber) => (axisNumber - 1) * 0x800;

    private static ushort Idx(ushort index, int axisNumber) => (ushort)(index + AxisOffset(axisNumber));

    private const ushort Cw = 0x6040, Sw = 0x6041;
    private const ushort ModeSet = 0x6060, ModeDisp = 0x6061;
    private const ushort PosDemand = 0x6062, PosActual = 0x6064;
    private const ushort VelActual = 0x606C, TorqueActual = 0x6077;
    private const ushort TargetPos = 0x607A, TargetVel = 0x60FF, TargetTorque = 0x6071;
    private const ushort ProfileVel = 0x6081, ProfileAcc = 0x6083, ProfileDec = 0x6084;
    private const ushort HomingMethod = 0x6098, HomeOffset = 0x607C;
    private const ushort PosWindow = 0x6067;
    private const ushort FollowingError = 0x60F4;
    private const ushort ErrorCode = 0x603F;
    private const ushort DigitalInputsIndex = 0x60FD;
    private const ushort ProbeFunction = 0x60B8, ProbeStatus = 0x60B9;
    private const ushort ProbePos1Pos = 0x60BA, ProbePos1Neg = 0x60BB;

    // ------------------------------------------------------------ ISlaveBehavior

    public void OnOutputsWritten(SimulatedSlave slave, double dtSeconds)
    {
        foreach (var axis in _axes)
            ReadCommands(slave, axis);
    }

    public void Simulate(SimulatedSlave slave, double dtSeconds)
    {
        double dt = dtSeconds > 0 ? Math.Min(dtSeconds, 0.05) : 0.001;
        foreach (var axis in _axes)
        {
            if (axis.SubState == CiA402SubState.OperationEnabled)
                Move(slave, axis, dt);
            else if (axis.HomingActive)
                axis.HomingActive = false;
            else
                axis.Velocity = BehaviorMath.ApproachZero(axis.Velocity, _options.MaxAcceleration * dt);
        }
    }

    public void BeforeInputsRead(SimulatedSlave slave, double dtSeconds)
    {
        foreach (var axis in _axes)
            Publish(slave, axis);
    }

    public string Describe()
    {
        var sb = new StringBuilder();
        foreach (var axis in _axes)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"轴{axis.AxisNumber}:{axis.SubState} 位置={axis.Position:F0} 速度={axis.Velocity:F0} ");
        }
        return sb.ToString().TrimEnd();
    }

    // ------------------------------------------------------------ 命令解析

    private void ReadCommands(SimulatedSlave slave, VirtualDriveAxis axis)
    {
        int n = axis.AxisNumber;
        ushort controlword = (ushort)slave.GetObjectValue(Idx(Cw, n), 0);
        byte mode = (byte)slave.GetObjectValue(Idx(ModeSet, n), 0);

        bool faultReset = (controlword & 0x0080) != 0;
        byte command = (byte)(controlword & 0x000F);
        bool quickStop = (controlword & 0x0004) == 0;

        if (faultReset && axis.Fault)
        {
            axis.Fault = false;
            axis.ErrorCode = 0;
            axis.SubState = CiA402SubState.SwitchOnDisabled;
            axis.Warning = false;
        }

        // 状态机（CiA402 power drive state machine）
        if (!axis.Fault)
        {
            switch (command)
            {
                case 0x00:
                case 0x01:
                    axis.SubState = CiA402SubState.SwitchOnDisabled;
                    break;
                case 0x02:
                    axis.SubState = quickStop ? CiA402SubState.QuickStopActive : axis.SubState;
                    if (quickStop && axis.SubState != CiA402SubState.OperationEnabled)
                        axis.SubState = CiA402SubState.QuickStopActive;
                    break;
                case 0x06:
                    axis.SubState = CiA402SubState.ReadyToSwitchOn;
                    break;
                case 0x07:
                    axis.SubState = axis.SubState is CiA402SubState.ReadyToSwitchOn or CiA402SubState.SwitchedOn
                        or CiA402SubState.OperationEnabled
                        ? CiA402SubState.SwitchedOn
                        : axis.SubState;
                    break;
                case 0x0F:
                    axis.SubState = axis.SubState is CiA402SubState.SwitchedOn or CiA402SubState.OperationEnabled
                        ? CiA402SubState.OperationEnabled
                        : axis.SubState;
                    break;
            }
        }

        if (axis.SubState != CiA402SubState.OperationEnabled)
        {
            axis.TargetReached = false;
            axis.Velocity = 0;
        }

        axis.Mode = mode;
        slave.SetObjectValue(Idx(ModeDisp, n), 0, mode, 1);

        // 模式切换会清除"完成"标志
        if (mode != 6)
            axis.HomingAttained = false;

        // 回零启动：HM 模式下控制字 bit4 上升沿
        bool newSetPoint = (controlword & 0x0010) != 0;
        if (mode == 6 && axis.SubState == CiA402SubState.OperationEnabled &&
            newSetPoint && !axis.LastNewSetPoint && !axis.HomingAttained)
        {
            axis.HomingActive = true;
            axis.HomingElapsed = 0;
        }
        if (!newSetPoint)
            axis.HomingActive = false;
        axis.LastNewSetPoint = newSetPoint;

        // 探针：0x60B8 bit0 使能单次正沿捕获
        ushort probeFunc = (ushort)slave.GetObjectValue(Idx(ProbeFunction, n), 0);
        axis.ProbeArmed = (probeFunc & 0x0001) != 0;

        // 传感器：优先使用外部强制值，否则按位置自动判定
        axis.NegativeLimitSwitch = axis.NegativeLimitOverride ?? axis.Position <= _options.NegativeLimit;
        axis.PositiveLimitSwitch = axis.PositiveLimitOverride ?? axis.Position >= _options.PositiveLimit;
        axis.HomeSwitch = axis.HomeSwitchOverride ?? Math.Abs(axis.Position) <= _options.HomeSwitchWindow;

        axis.Warning = Math.Abs(axis.Torque) > 0.9;
    }

    // ------------------------------------------------------------ 运动学

    private void Move(SimulatedSlave slave, VirtualDriveAxis axis, double dt)
    {
        int n = axis.AxisNumber;

        if (axis.Fault)
        {
            axis.Velocity = 0;
            return;
        }

        double previousVelocity = axis.Velocity;
        bool halt = ((ushort)slave.GetObjectValue(Idx(Cw, n), 0) & 0x0100) != 0;

        double profileVelocity = ReadU32OrDefault(slave, Idx(ProfileVel, n), 200_000);
        double acceleration = ReadU32OrDefault(slave, Idx(ProfileAcc, n), _options.MaxAcceleration / 2);
        double deceleration = ReadU32OrDefault(slave, Idx(ProfileDec, n), acceleration);
        if (acceleration <= 0) acceleration = _options.MaxAcceleration / 2;
        if (deceleration <= 0) deceleration = acceleration;

        switch (axis.Mode)
        {
            case 1:   // profile position
                MoveProfilePosition(slave, axis, dt, halt, profileVelocity, acceleration, deceleration);
                break;
            case 3:   // profile velocity
                MoveProfileVelocity(slave, axis, dt, halt, acceleration, deceleration);
                break;
            case 6:   // homing
                MoveHoming(slave, axis, dt);
                break;
            case 8:   // cyclic synchronous position
                MoveCsp(slave, axis, dt, halt);
                break;
            case 9:   // cyclic synchronous velocity
                MoveCsv(slave, axis, dt, halt, acceleration, deceleration);
                break;
            case 10:  // cyclic synchronous torque
                MoveCst(slave, axis, dt, halt);
                break;
            default:
                axis.Velocity = BehaviorMath.ApproachZero(axis.Velocity, deceleration * dt);
                break;
        }

        axis.Position += axis.Velocity * dt;
        axis.CommandPosition += axis.Velocity * dt;

        // 软限位
        if (axis.Position >= _options.PositiveLimit || axis.Position <= _options.NegativeLimit)
        {
            axis.Position = BehaviorMath.Clamp(axis.Position, _options.NegativeLimit, _options.PositiveLimit);
            axis.Velocity = 0;
            RaiseFault(axis, 0x8612);   // 软件限位越界
            return;
        }

        // 跟随误差
        if (_options.FollowingErrorWindow > 0)
        {
            double error = axis.CommandPosition - axis.Position;
            if (Math.Abs(error) > _options.FollowingErrorWindow)
                RaiseFault(axis, 0x8611);   // 跟随误差超限
        }

        // 转矩估算：加速 + 摩擦 + 负载
        double accel = dt > 0 ? (axis.Velocity - previousVelocity) / dt : 0;
        double torque = (accel / _options.MaxAcceleration) * _options.Inertia
                        + (axis.Velocity / _options.MaxVelocity) * _options.ViscousFriction
                        + _options.LoadTorque;
        axis.Torque = BehaviorMath.Clamp(torque, -1.2, 1.2);

        // 探针捕获（正沿）
        if (axis.ProbeArmed && !axis.ProbeCaptured && axis.ProbeInput && !axis.ProbeLastInput)
        {
            axis.ProbeCaptured = true;
            axis.ProbeCapturedPosition = (int)Math.Round(axis.Position);
        }
        axis.ProbeLastInput = axis.ProbeInput;
    }

    private void MoveProfilePosition(SimulatedSlave slave, VirtualDriveAxis axis, double dt,
        bool halt, double profileVelocity, double acceleration, double deceleration)
    {
        int n = axis.AxisNumber;
        int window = (int)ReadU32OrDefault(slave, Idx(PosWindow, n), (uint)_options.PositionWindow);
        if (window <= 0) window = _options.PositionWindow;

        long target = slave.GetObjectSignedValue(Idx(TargetPos, n), 0);
        double remaining = target - axis.Position;

        if (halt)
        {
            axis.Velocity = BehaviorMath.ApproachZero(axis.Velocity, deceleration * dt);
            axis.TargetReached = Math.Abs(remaining) <= window && axis.Velocity == 0;
            return;
        }

        // 梯形速度规划：先判断是否进入减速段
        double stopDistance = axis.Velocity * axis.Velocity / (2 * deceleration);
        if (Math.Abs(remaining) <= stopDistance)
            axis.Velocity = BehaviorMath.ApproachZero(axis.Velocity, deceleration * dt);
        else
        {
            double direction = Math.Sign(remaining);
            axis.Velocity += direction * acceleration * dt;
            axis.Velocity = BehaviorMath.Clamp(axis.Velocity, -profileVelocity, profileVelocity);
        }

        // 防止越过目标
        if (Math.Abs(axis.Velocity * dt) >= Math.Abs(remaining) && Math.Sign(axis.Velocity) == Math.Sign(remaining))
        {
            axis.Position = target;
            axis.CommandPosition = target;
            axis.Velocity = 0;
        }

        axis.TargetReached = Math.Abs(target - axis.Position) <= window && Math.Abs(axis.Velocity) < 1;
    }

    private void MoveProfileVelocity(SimulatedSlave slave, VirtualDriveAxis axis, double dt,
        bool halt, double acceleration, double deceleration)
    {
        int n = axis.AxisNumber;
        int target = halt ? 0 : (int)slave.GetObjectSignedValue(Idx(TargetVel, n), 0);
        target = (int)BehaviorMath.Clamp(target, -_options.MaxVelocity, _options.MaxVelocity);

        double step = (Math.Abs(target) > Math.Abs(axis.Velocity) ? acceleration : deceleration) * dt;
        axis.Velocity = Approach(axis.Velocity, target, step);
        axis.TargetReached = false;
    }

    private void MoveCsv(SimulatedSlave slave, VirtualDriveAxis axis, double dt,
        bool halt, double acceleration, double deceleration)
    {
        int n = axis.AxisNumber;
        int target = halt ? 0 : (int)slave.GetObjectSignedValue(Idx(TargetVel, n), 0);
        target = (int)BehaviorMath.Clamp(target, -_options.MaxVelocity, _options.MaxVelocity);
        double step = (Math.Abs(target) > Math.Abs(axis.Velocity) ? acceleration : deceleration) * dt;
        axis.Velocity = Approach(axis.Velocity, target, step);
        axis.TargetReached = Math.Abs(axis.Velocity) < 1;
    }

    private void MoveCsp(SimulatedSlave slave, VirtualDriveAxis axis, double dt, bool halt)
    {
        int n = axis.AxisNumber;
        long target = halt ? (long)axis.Position : slave.GetObjectSignedValue(Idx(TargetPos, n), 0);
        double error = target - axis.Position;

        // 一阶位置环：速度 = 误差 × 位置环增益，再限幅
        double desired = error * _options.PositionLoopGain;
        desired = BehaviorMath.Clamp(desired, -_options.MaxVelocity, _options.MaxVelocity);
        axis.Velocity = desired;
        axis.CommandPosition = target;
        axis.TargetReached = Math.Abs(error) <= _options.PositionWindow;
    }

    private void MoveCst(SimulatedSlave slave, VirtualDriveAxis axis, double dt, bool halt)
    {
        int n = axis.AxisNumber;
        short command = halt ? (short)0 : (short)slave.GetObjectSignedValue(Idx(TargetTorque, n), 0);
        double torque = command / 1000.0;

        double accel = (torque - _options.ViscousFriction * (axis.Velocity / _options.MaxVelocity)
                        - _options.LoadTorque) / _options.Inertia * _options.MaxAcceleration;
        axis.Velocity += accel * dt;
        axis.Velocity = BehaviorMath.Clamp(axis.Velocity, -_options.MaxVelocity, _options.MaxVelocity);
        axis.Torque = torque;
        axis.TargetReached = Math.Abs(command) < 1;
    }

    private void MoveHoming(SimulatedSlave slave, VirtualDriveAxis axis, double dt)
    {
        int n = axis.AxisNumber;
        if (!axis.HomingActive)
        {
            axis.Velocity = BehaviorMath.ApproachZero(axis.Velocity, _options.MaxAcceleration * dt);
            return;
        }

        axis.HomingElapsed += dt;
        int method = (byte)slave.GetObjectValue(Idx(HomingMethod, n), 0);
        long offset = slave.GetObjectSignedValue(Idx(HomeOffset, n), 0);

        // 方法 35/37：以当前位置为原点
        if (method is 35 or 37)
        {
            axis.Position = offset;
            axis.CommandPosition = offset;
            axis.Velocity = 0;
            axis.HomingActive = false;
            axis.HomingAttained = true;
            return;
        }

        // 简化规则（覆盖常用方法）：奇数朝负向，1/2/7/8/23/24 找限位，其余找原点开关
        bool negative = method is 1 or 3 or 5 or 7 or 17 or 19 or 21 or 23 or 33;
        bool useLimit = method is 1 or 2 or 7 or 8 or 23 or 24;
        // 0x6099:1 = 搜索开关速度，0x6099:2 = 搜索零脉冲速度
        double speed = ReadU32OrDefault(slave, (ushort)(0x6099 + AxisOffset(n)), 1, 0);
        if (speed <= 0) speed = ReadU32OrDefault(slave, (ushort)(0x6099 + AxisOffset(n)), 2, 100_000);
        if (speed <= 0) speed = 100_000;

        axis.Velocity = negative ? -speed : speed;

        bool reached;
        if (useLimit)
            reached = negative ? axis.NegativeLimitSwitch : axis.PositiveLimitSwitch;
        else
            reached = axis.HomeSwitch ||
                      (negative ? axis.NegativeLimitSwitch : axis.PositiveLimitSwitch);

        if (reached)
        {
            axis.Position = offset;
            axis.CommandPosition = offset;
            axis.Velocity = 0;
            axis.HomingActive = false;
            axis.HomingAttained = true;
            axis.TargetReached = true;
            return;
        }

        if (axis.HomingElapsed > _options.HomingTimeout)
            RaiseFault(axis, 0x8612);
    }

    private static double Approach(double current, double target, double step)
    {
        if (Math.Abs(target - current) <= step) return target;
        return current < target ? current + step : current - step;
    }

    private void RaiseFault(VirtualDriveAxis axis, ushort code)
    {
        axis.Fault = true;
        axis.ErrorCode = code;
        axis.SubState = CiA402SubState.Fault;
        axis.Velocity = 0;
        axis.HomingActive = false;
        axis.TargetReached = false;
    }

    // ------------------------------------------------------------ 输入上报

    private void Publish(SimulatedSlave slave, VirtualDriveAxis axis)
    {
        int n = axis.AxisNumber;

        ushort statusword = 0;
        switch (axis.SubState)
        {
            case CiA402SubState.SwitchOnDisabled: statusword = 0x0040; break;
            case CiA402SubState.ReadyToSwitchOn: statusword = 0x0021; break;
            case CiA402SubState.SwitchedOn: statusword = 0x0023; break;
            case CiA402SubState.OperationEnabled: statusword = 0x0027; break;
            case CiA402SubState.QuickStopActive: statusword = 0x0007; break;
            case CiA402SubState.FaultReactionActive:
            case CiA402SubState.Fault: statusword = 0x0008; break;
        }
        statusword |= 0x0210;   // bit4 主回路电压就绪 + bit9 远程控制
        if (axis.TargetReached) statusword |= 0x0400;
        if (axis.HomingAttained) statusword |= 0x1000;
        if (axis.Warning) statusword |= 0x0080;
        if (axis.Fault) statusword |= 0x0008;

        slave.SetObjectValue(Idx(Sw, n), 0, statusword, 2);
        slave.SetObjectValue(Idx(ErrorCode, n), 0, axis.ErrorCode, 2);
        slave.SetObjectValue(Idx(PosActual, n), 0, unchecked((ulong)(long)Math.Round(axis.Position)), 4);
        slave.SetObjectValue(Idx(VelActual, n), 0, unchecked((ulong)(long)Math.Round(axis.Velocity)), 4);
        slave.SetObjectValue(Idx(PosDemand, n), 0, unchecked((ulong)(long)Math.Round(axis.CommandPosition)), 4);
        slave.SetObjectValue(Idx(TorqueActual, n), 0, unchecked((ulong)(short)Math.Round(axis.Torque * 1000)), 2);
        slave.SetObjectValue(Idx(FollowingError, n), 0,
            unchecked((ulong)(long)Math.Round(axis.CommandPosition - axis.Position)), 4);

        uint digitalInputs = 0;
        if (axis.NegativeLimitSwitch) digitalInputs |= 0x0001;
        if (axis.PositiveLimitSwitch) digitalInputs |= 0x0002;
        if (axis.HomeSwitch) digitalInputs |= 0x0004;
        if (axis.ProbeInput) digitalInputs |= 0x00010000;
        slave.SetObjectValue(Idx(DigitalInputsIndex, n), 0, digitalInputs, 4);

        ushort probeStatus = 0;
        if (axis.ProbeArmed) probeStatus |= 0x0004;
        if (axis.ProbeCaptured) probeStatus |= 0x0001;
        slave.SetObjectValue(Idx(ProbeStatus, n), 0, probeStatus, 2);
        slave.SetObjectValue(Idx(ProbePos1Pos, n), 0,
            unchecked((ulong)(long)axis.ProbeCapturedPosition), 4);
        slave.SetObjectValue(Idx(ProbePos1Neg, n), 0, 0UL, 4);
    }

    private static double ReadU32OrDefault(SimulatedSlave slave, ushort index, double defaultValue)
    {
        ulong raw = slave.GetObjectValue(index, 0);
        if (raw == 0) return defaultValue;
        return unchecked((int)(uint)raw);
    }

    private static double ReadU32OrDefault(SimulatedSlave slave, ushort index, byte subIndex, double defaultValue)
    {
        ulong raw = slave.GetObjectValue(index, subIndex);
        if (raw == 0) return defaultValue;
        return unchecked((int)(uint)raw);
    }
}

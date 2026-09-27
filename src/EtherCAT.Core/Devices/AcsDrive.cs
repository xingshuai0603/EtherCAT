using System;
using System.Threading;
using EtherCAT.Master;

namespace EtherCAT.Devices;

/// <summary>
/// ACS 驱动器高层操作封装（基于标准 CiA402 / DS402）。
/// 适用于所有兼容 DS402 的 EtherCAT 伺服（含 ACS 品牌硬件）：
/// 在 CiA402Drive 之上提供更贴近“操作一台驱动器”的语义——使能、使能并回零、
/// 点动、绝对/相对定位、速度模式、转矩模式，以及到位等待与故障复位。
///
/// 真实硬件与虚拟从站使用同一套代码，仅 ESI 描述文件不同；主站对 ACS 而言
/// 就是一个标准的 CiA402 从站，因此本项目里“ACS 驱动器”与“通用 CiA402 伺服”
/// 共享底层，差异只在对象字典/PDO 由对应的 ESI 决定。
/// </summary>
public sealed class AcsDrive
{
    public const string VendorName = "ACS";

    private readonly CiA402Drive _drive;
    private readonly Action<string>? _log;

    public AcsDrive(CiA402Drive drive, Action<string>? log = null)
    {
        _drive = drive ?? throw new ArgumentNullException(nameof(drive));
        _log = log;
    }

    public AcsDrive(EthercatMaster master, SlaveInfo slave, int axis = 1, Action<string>? log = null)
        : this(new CiA402Drive(master, slave, axis), log) { }

    public SlaveInfo Slave => _drive.Slave;
    public int SlaveIndex => _drive.SlaveIndex;
    public int AxisNumber => _drive.AxisNumber;
    public string DisplayName => _drive.DisplayName;

    // ------------------------------------------------------------------ 状态

    public string StateText => _drive.StateText;
    public ushort Statusword => _drive.Statusword;
    public bool Enabled => _drive.OperationEnabled;
    public bool Fault => _drive.Fault;
    public bool ReadyToSwitchOn => _drive.ReadyToSwitchOn;
    public bool SwitchedOn => _drive.SwitchedOn;
    public bool VoltageEnabled => _drive.VoltageEnabled;
    public bool QuickStopActive => _drive.QuickStopActive;
    public bool Warning => _drive.Warning;
    public bool TargetReached => _drive.TargetReached;
    public bool HomingAttained => _drive.HomingAttained;
    public bool FollowingError => _drive.FollowingError;
    public int Position => _drive.PositionActual;
    public int Velocity => _drive.VelocityActual;
    public short Torque => _drive.TorqueActual;
    public ushort ErrorCode => _drive.ErrorCode;
    public CiA402Mode Mode { get => _drive.Mode; set => _drive.Mode = value; }
    public int TargetPosition => _drive.TargetPosition;
    public int TargetVelocity => _drive.TargetVelocity;

    // ------------------------------------------------------------------ 使能/禁用

    public bool Enable(int timeoutMs = 3000)
    {
        _log?.Invoke($"[{DisplayName}] 使能伺服…");
        bool ok = _drive.Enable(timeoutMs);
        _log?.Invoke(ok
            ? $"[{DisplayName}] 已使能：{StateText} (0x{Statusword:X4})"
            : $"[{DisplayName}] 使能失败 (0x{Statusword:X4})");
        return ok;
    }

    public void Disable()
    {
        _drive.Disable();
        _log?.Invoke($"[{DisplayName}] 已断电 (0x{Statusword:X4})");
    }

    public void FaultReset()
    {
        _drive.FaultReset();
        _log?.Invoke($"[{DisplayName}] 故障复位 (0x{Statusword:X4})");
    }

    public void QuickStop()
    {
        _drive.QuickStop();
        _log?.Invoke($"[{DisplayName}] 急停");
    }

    /// <summary>暂停当前运动（保持使能），halt=true 时减速停止</summary>
    public void Halt(bool halt = true) => _drive.Halt(halt);

    /// <summary>停止并减速到零（保持使能）</summary>
    public void Stop() => Halt(true);

    // ------------------------------------------------------------------ 回零

    /// <summary>启动回零（需在已使能状态下调用）</summary>
    public void Home(byte method = 17)
    {
        _drive.Mode = CiA402Mode.Homing;
        _drive.StartHoming(method);
        _log?.Invoke($"[{DisplayName}] 启动回零 method={method}");
    }

    /// <summary>先使能，再回零并等待完成。最常用的一键上电归零流程。</summary>
    public bool EnableAndHome(byte method = 17, int enableTimeoutMs = 3000, int homeTimeoutMs = 15000)
    {
        if (!Enable(enableTimeoutMs))
            return false;
        Home(method);
        return WaitHomed(homeTimeoutMs);
    }

    /// <summary>阻塞等待回零完成；若中途报错会自动尝试复位一次。</summary>
    public bool WaitHomed(int timeoutMs = 15000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (_drive.HomingAttained)
            {
                _log?.Invoke($"[{DisplayName}] 回零完成 pos={Position}");
                return true;
            }
            if (_drive.Fault)
            {
                _log?.Invoke($"[{DisplayName}] 回零中发生故障 0x{ErrorCode:X4}，尝试复位");
                _drive.FaultReset();
            }
            Thread.Sleep(20);
        }
        _log?.Invoke($"[{DisplayName}] 回零超时（{timeoutMs}ms），当前 pos={Position}");
        return _drive.HomingAttained;
    }

    // ------------------------------------------------------------------ 点动 / 速度

    /// <summary>点动：进入速度模式并以给定速度连续运行（正负号决定方向）</summary>
    public void Jog(int velocity)
    {
        _drive.Mode = CiA402Mode.ProfileVelocity;
        _drive.SetTargetVelocity(velocity);
        _drive.Halt(false);
        _log?.Invoke($"[{DisplayName}] 点动 velocity={velocity}");
    }

    /// <summary>速度模式：以给定目标速度运行</summary>
    public void SetVelocity(int velocity)
    {
        _drive.Mode = CiA402Mode.ProfileVelocity;
        _drive.SetTargetVelocity(velocity);
        _drive.Halt(false);
        _log?.Invoke($"[{DisplayName}] 速度模式 velocity={velocity}");
    }

    // ------------------------------------------------------------------ 定位

    /// <summary>
    /// 绝对定位：进入轮廓位置模式，按需要写入速度/加减速，再写入目标位置并触发新设定点。
    /// DS402 要求“新设定点”位(0x10)产生 0→1 上升沿才会启动一次新的定位。
    /// </summary>
    public void MoveAbsolute(int position,
        uint? velocity = null, uint? acceleration = null, uint? deceleration = null)
    {
        _drive.Mode = CiA402Mode.ProfilePosition;
        if (velocity.HasValue) _drive.SetProfileVelocity(velocity.Value);
        if (acceleration.HasValue) _drive.SetProfileAcceleration(acceleration.Value);
        if (deceleration.HasValue) _drive.SetProfileDeceleration(deceleration.Value);
        _drive.SetTargetPosition(position);
        TriggerNewSetpoint();
        _log?.Invoke($"[{DisplayName}] 绝对定位 target={position} vel={velocity}");
    }

    /// <summary>相对定位：以当前实际位置为基准偏移 distance</summary>
    public void MoveRelative(int distance,
        uint? velocity = null, uint? acceleration = null, uint? deceleration = null)
    {
        MoveAbsolute(Position + distance, velocity, acceleration, deceleration);
    }

    /// <summary>阻塞等待到位（TargetReached），超时或故障返回 false</summary>
    public bool WaitTargetReached(int timeoutMs = 10000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (_drive.TargetReached) return true;
            if (_drive.Fault) return false;
            Thread.Sleep(10);
        }
        return _drive.TargetReached;
    }

    // ------------------------------------------------------------------ 转矩

    /// <summary>转矩模式（循环同步转矩，单位：额定转矩的千分之一，如 200 = 20% 额定）</summary>
    public void SetTorque(short torque)
    {
        _drive.Mode = CiA402Mode.CyclicSynchronousTorque;
        _drive.SetTargetTorque(torque);
        _drive.Halt(false);
        _log?.Invoke($"[{DisplayName}] 转矩模式 torque={torque}");
    }

    // ------------------------------------------------------------------ 内部

    private void TriggerNewSetpoint()
    {
        // DS402 规定每次新定位需要“新设定点”位(0x10)产生 0→1 上升沿。
        // 先写 0（保持使能 0x000F），留出过程数据帧发送时间后再写 1（0x001F）。
        // 周期通信已在运行时该上升沿会被从站采样到；虚拟从站则直接按目标位置运动。
        _drive.Controlword = 0x000F;
        Thread.Sleep(10);
        _drive.Controlword = 0x001F;
    }

    public override string ToString() => $"ACS #{SlaveIndex} {DisplayName}";
}

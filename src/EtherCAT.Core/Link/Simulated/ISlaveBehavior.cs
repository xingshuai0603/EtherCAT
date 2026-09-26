namespace EtherCAT.Link.Simulated;

/// <summary>
/// 仿真从站的可插拔行为模型：把"从站做什么"从"ESC 寄存器/邮箱协议"里剥离出来，
/// 这样可以独立扩展出大型 IO 模块、多轴伺服等虚拟设备，而不用改协议层。
/// </summary>
public interface ISlaveBehavior
{
    /// <summary>主站写入输出过程数据（SM2）之后调用：解析命令、更新内部状态</summary>
    void OnOutputsWritten(SimulatedSlave slave, double dtSeconds);

    /// <summary>主站读取输入过程数据（SM3）之前调用：刷新需要上报的物理量</summary>
    void BeforeInputsRead(SimulatedSlave slave, double dtSeconds);

    /// <summary>每个通信周期推进一次物理仿真（积分运动、波形、计数器等）</summary>
    void Simulate(SimulatedSlave slave, double dtSeconds);

    /// <summary>供 UI / 日志显示的内部状态摘要</summary>
    string Describe();
}

/// <summary>
/// 行为模型通用的小工具：把 BCD/有符号数与对象字典字节数组互换
/// </summary>
public static class BehaviorMath
{
    /// <summary>按字节数做符号扩展</summary>
    public static long SignExtend(ulong value, int size)
    {
        return size switch
        {
            1 => unchecked((sbyte)(byte)value),
            2 => unchecked((short)(ushort)value),
            4 => unchecked((int)(uint)value),
            _ => unchecked((long)value)
        };
    }

    public static ulong Truncate(long value, int size)
    {
        return size switch
        {
            1 => (byte)value,
            2 => (ushort)value,
            4 => (uint)value,
            _ => (ulong)value
        };
    }

    public static double Clamp(double value, double min, double max) =>
        value < min ? min : value > max ? max : value;

    /// <summary>向 0 逼近一个步长，避免越过零点来回抖动</summary>
    public static double ApproachZero(double value, double step)
    {
        if (Math.Abs(value) <= step) return 0;
        return value > 0 ? value - step : value + step;
    }
}

namespace EtherCAT;

/// <summary>
/// EtherCAT 主站异常
/// </summary>
public class EthercatException : Exception
{
    public EthercatException(string message) : base(message) { }
    public EthercatException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// 简单日志接口（UI 与控制台各自实现）
/// </summary>
public interface IEthercatLog
{
    void Log(string message);
}

/// <summary>空日志实现</summary>
public sealed class NullLog : IEthercatLog
{
    public static readonly NullLog Instance = new();
    public void Log(string message) { }
}

/// <summary>把日志转发给委托</summary>
public sealed class DelegateLog : IEthercatLog
{
    private readonly Action<string> _sink;
    public DelegateLog(Action<string> sink) => _sink = sink;
    public void Log(string message) => _sink(message);
}

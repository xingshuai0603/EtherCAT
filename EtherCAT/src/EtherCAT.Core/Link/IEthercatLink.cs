namespace EtherCAT.Link;

/// <summary>
/// 网卡信息
/// </summary>
public sealed class NetworkAdapterInfo
{
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;

    public override string ToString() => string.IsNullOrWhiteSpace(Description) ? Name : $"{Name}  ({Description})";
}

/// <summary>
/// EtherCAT 链路抽象：负责把原始以太网帧（EtherType 0x88A4）发出并取回响应
/// </summary>
public interface IEthercatLink : IDisposable
{
    /// <summary>链路显示名</summary>
    string Name { get; }

    bool IsOpen { get; }

    /// <summary>打开链路（device 为网卡标识，如 \Device\NPF_{GUID}）</summary>
    void Open(string device);

    void Close();

    /// <summary>
    /// 发送一帧并等待返回帧
    /// </summary>
    /// <returns>返回帧长度；0 表示超时</returns>
    int Transceive(byte[] frame, int frameLength, byte[] response, int timeoutUs);

    /// <summary>枚举可用网卡</summary>
    IReadOnlyList<NetworkAdapterInfo> EnumerateAdapters();
}

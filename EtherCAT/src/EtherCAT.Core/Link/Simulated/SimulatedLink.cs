using System.Buffers.Binary;
using System.Diagnostics;
using EtherCAT.Esi;
using EtherCAT.Protocol;

namespace EtherCAT.Link.Simulated;

/// <summary>
/// 仿真链路：把主站发出的 EtherCAT 帧交给一组仿真从站处理，用于无硬件环境验证
/// </summary>
public sealed class SimulatedLink : IEthercatLink
{
    private readonly List<SimulatedSlave> _slaves = new();
    private readonly Stopwatch _cycleTimer = Stopwatch.StartNew();
    private bool _open;

    public string Name => "Simulated (virtual slaves)";

    public bool IsOpen => _open;

    public IReadOnlyList<SimulatedSlave> Slaves => _slaves;

    /// <summary>
    /// 用一组 ESI 设备描述创建仿真网络。
    /// 若描述属于内置虚拟设备（VirtualDeviceFactory），会自动挂接对应的行为模型。
    /// </summary>
    public static SimulatedLink CreateFromEsi(IEnumerable<EsiDevice> devices, int repeat = 1,
        VirtualDriveOptions? driveOptions = null, VirtualIoOptions? ioOptions = null)
    {
        var link = new SimulatedLink();
        int position = 1;
        for (int r = 0; r < Math.Max(1, repeat); r++)
        {
            foreach (var device in devices)
            {
                if (!VirtualDeviceFactory.TryCreateFromEsi(device, position, out var slave, driveOptions, ioOptions))
                    slave = SimulatedSlave.FromEsi(device, position);
                link._slaves.Add(slave!);
                position++;
            }
        }
        return link;
    }

    public void AddSlave(SimulatedSlave slave) => _slaves.Add(slave);

    public void Open(string device)
    {
        _open = true;
        _cycleTimer.Restart();
    }

    public void Close() => _open = false;

    private readonly object _sync = new();

    public int Transceive(byte[] frame, int frameLength, byte[] response, int timeoutUs)
    {
        // 周期线程(PDO)与主线程(SDO/邮箱)会并发访问同一份仿真 DPRAM，这里串行化
        lock (_sync)
        {
            return TransceiveCore(frame, frameLength, response, timeoutUs);
        }
    }

    private int TransceiveCore(byte[] frame, int frameLength, byte[] response, int timeoutUs)
    {
        if (!_open)
            return 0;

        Array.Copy(frame, response, frameLength);

        int pos = Esc.EthernetHeaderSize + Esc.EthercatHeaderSize;
        bool anyLogical = false;

        while (pos + Esc.DatagramHeaderSize + Esc.WorkCounterSize <= frameLength)
        {
            var command = (EthercatCommand)response[pos];
            ushort adp = BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(pos + 2, 2));
            ushort ado = BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(pos + 4, 2));
            ushort lengthFlag = BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(pos + 6, 2));
            int dataLength = lengthFlag & 0x7FFF;
            int payloadOffset = pos + Esc.DatagramHeaderSize;
            if (payloadOffset + dataLength + Esc.WorkCounterSize > frameLength)
                break;

            int workingCounter = 0;
            switch (command)
            {
                case EthercatCommand.Lrd:
                case EthercatCommand.Lwr:
                case EthercatCommand.Lrw:
                {
                    uint logicalAddress = (uint)(adp | (ado << 16));
                    bool doRead = command is EthercatCommand.Lrd or EthercatCommand.Lrw;
                    bool doWrite = command is EthercatCommand.Lwr or EthercatCommand.Lrw;
                    foreach (var slave in _slaves)
                        slave.ProcessLogical(doRead, doWrite, logicalAddress, response, payloadOffset, dataLength, ref workingCounter);
                    anyLogical = true;
                    break;
                }
                case EthercatCommand.Armw:
                case EthercatCommand.Frmw:
                {
                    ushort adpLocal = adp;
                    foreach (var slave in _slaves)
                    {
                        slave.ProcessDatagram(command, ref adpLocal, ado, response, payloadOffset, dataLength, ref workingCounter);
                        if (command == EthercatCommand.Frmw)
                            adpLocal = adp;   // 配置寻址不做自增
                    }
                    break;
                }
                default:
                {
                    ushort adpLocal = adp;
                    foreach (var slave in _slaves)
                        slave.ProcessDatagram(command, ref adpLocal, ado, response, payloadOffset, dataLength, ref workingCounter);
                    break;
                }
            }

            BinaryPrimitives.WriteUInt16LittleEndian(response.AsSpan(payloadOffset + dataLength, 2), (ushort)workingCounter);

            bool more = (lengthFlag & Esc.DatagramFollows) != 0;
            pos = payloadOffset + dataLength + Esc.WorkCounterSize;
            if (!more)
                break;
        }

        if (anyLogical)
        {
            double dt = _cycleTimer.Elapsed.TotalSeconds;
            _cycleTimer.Restart();
            if (dt > 0.5) dt = 0.001;
            foreach (var slave in _slaves)
                slave.Simulate(dt);
        }

        return frameLength;
    }

    public IReadOnlyList<NetworkAdapterInfo> EnumerateAdapters() =>
        new List<NetworkAdapterInfo>
        {
            new() { Name = "sim0", Description = "仿真网络（虚拟从站）" }
        };

    public void Dispose() => Close();
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using EtherCAT.Protocol;

namespace EtherCAT.Link;

/// <summary>
/// wpcap / npcap 原生接口（SOEM 在 Windows 上同样基于 winpcap/npcap 发送原始以太网帧）
/// </summary>
internal static class NpcapNative
{
    public const int Promiscuous = 1;
    public const int DataTxUdp = 2;
    public const int NoCaptureRpcap = 4;
    public const int NoCaptureLocal = 8;
    public const int MaxResponsiveness = 16;

    public const int ErrorBufferSize = 256;

    private static bool _resolved;

    /// <summary>
    /// Npcap 默认把 wpcap.dll 装在 System32\Npcap，WinPcap 兼容模式才在 System32 根目录，
    /// 这里依次尝试，成功后 DllImport("wpcap") 即可命中已加载模块。
    /// </summary>
    internal static void EnsureLoaded()
    {
        if (_resolved) return;
        _resolved = true;
        foreach (var path in new[]
                 {
                     "wpcap",
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "Npcap", "wpcap.dll"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wpcap.dll"),
                     @"C:\Windows\SysWOW64\wpcap.dll"
                 })
        {
            try
            {
                if (NativeLibrary.TryLoad(path, out _))
                    return;
            }
            catch (DllNotFoundException)
            {
                // 继续尝试下一个候选
            }
        }
    }

    [DllImport("wpcap", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr pcap_open(string source, int snaplen, int flags, int readTimeout, IntPtr auth, StringBuilder errbuf);

    [DllImport("wpcap", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void pcap_close(IntPtr handle);

    [DllImport("wpcap", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int pcap_sendpacket(IntPtr handle, byte[] buffer, int size);

    [DllImport("wpcap", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int pcap_next_ex(IntPtr handle, out IntPtr header, out IntPtr data);

    [DllImport("wpcap", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int pcap_findalldevs_ex(string source, IntPtr auth, out IntPtr alldevs, StringBuilder errbuf);

    [DllImport("wpcap", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void pcap_freealldevs(IntPtr alldevs);

    [DllImport("wpcap", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr pcap_geterr(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    internal struct PcapIf
    {
        public IntPtr Next;
        public IntPtr Name;
        public IntPtr Description;
        public IntPtr Addresses;
        public uint Flags;
    }

    internal static string? PtrToUtf8(IntPtr ptr) =>
        ptr == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(ptr);
}

/// <summary>
/// 基于 Npcap/WinPcap 的原始以太网链路，可驱动真实 EtherCAT 网络
/// </summary>
public sealed class NpcapLink : IEthercatLink
{
    private IntPtr _handle;
    private readonly byte[] _response = new byte[Esc.MaxFrameSize];

    public string Name => "Npcap (raw ethernet)";

    public bool IsOpen => _handle != IntPtr.Zero;

    public void Open(string device)
    {
        NpcapNative.EnsureLoaded();
        if (IsOpen) Close();

        var errbuf = new StringBuilder(NpcapNative.ErrorBufferSize);
        _handle = NpcapNative.pcap_open(device, 65536,
            NpcapNative.Promiscuous | NpcapNative.MaxResponsiveness | NpcapNative.NoCaptureLocal,
            1, IntPtr.Zero, errbuf);
        if (_handle == IntPtr.Zero)
            throw new EthercatException($"无法打开网卡 {device}：{errbuf}。请确认已安装 Npcap(https://npcap.com) 并以管理员权限运行。");
    }

    public void Close()
    {
        if (_handle != IntPtr.Zero)
        {
            NpcapNative.pcap_close(_handle);
            _handle = IntPtr.Zero;
        }
    }

    public int Transceive(byte[] frame, int frameLength, byte[] response, int timeoutUs)
    {
        if (!IsOpen) throw new EthercatException("链路未打开");

        if (NpcapNative.pcap_sendpacket(_handle, frame, frameLength) != 0)
            return 0;

        var sw = Stopwatch.StartNew();
        long timeoutTicks = Stopwatch.Frequency * Math.Max(1, timeoutUs) / 1_000_000L;
        do
        {
            int res = NpcapNative.pcap_next_ex(_handle, out IntPtr header, out IntPtr data);
            if (res > 0 && data != IntPtr.Zero && header != IntPtr.Zero)
            {
                int len = ReadFrameLength(header);
                if (len > 0 && len <= response.Length)
                {
                    Marshal.Copy(data, response, 0, len);
                    if (IsEthercatFrame(response, len))
                        return len;
                }
            }
            else if (res < 0)
            {
                break;
            }
        } while (sw.ElapsedTicks < timeoutTicks);

        return 0;
    }

    private static int ReadFrameLength(IntPtr header)
    {
        // header 指向 pcap_pkthdr：ts.tv_sec(4/8) ts.tv_usec(4/8) caplen(4) len(4)
        return Marshal.ReadInt32(header, IntPtr.Size == 8 ? 16 : 8);
    }

    private static bool IsEthercatFrame(byte[] frame, int length)
    {
        if (length < 16) return false;
        return frame[12] == 0x88 && frame[13] == 0xA4;
    }

    public IReadOnlyList<NetworkAdapterInfo> EnumerateAdapters()
    {
        NpcapNative.EnsureLoaded();
        var list = new List<NetworkAdapterInfo>();
        var errbuf = new StringBuilder(NpcapNative.ErrorBufferSize);
        try
        {
            if (NpcapNative.pcap_findalldevs_ex("rpcap://", IntPtr.Zero, out IntPtr alldevs, errbuf) != 0)
                return list;

            IntPtr current = alldevs;
            while (current != IntPtr.Zero)
            {
                var dev = Marshal.PtrToStructure<NpcapNative.PcapIf>(current);
                var name = NpcapNative.PtrToUtf8(dev.Name);
                var desc = NpcapNative.PtrToUtf8(dev.Description);
                if (!string.IsNullOrEmpty(name))
                    list.Add(new NetworkAdapterInfo { Name = name!, Description = desc ?? string.Empty });
                current = dev.Next;
            }
            NpcapNative.pcap_freealldevs(alldevs);
        }
        catch (DllNotFoundException)
        {
            // Npcap 未安装
        }
        catch (EntryPointNotFoundException)
        {
            // 老版本 wpcap
        }

        return list;
    }

    public void Dispose() => Close();
}

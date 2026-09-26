using System.Globalization;
using System.Text;
using System.Xml.Linq;
using EtherCAT.Esi;

namespace EtherCAT.Link.Simulated;

/// <summary>内置虚拟设备种类</summary>
public enum VirtualDeviceKind
{
    /// <summary>大型单轴伺服（完整 CiA402 对象集 + 探针 + 诊断）</summary>
    BigServo,
    /// <summary>双轴伺服（第二轴对象索引 +0x800）</summary>
    DualAxisServo,
    /// <summary>64 点数字量 IO（64 DI / 64 DO）</summary>
    DigitalIo64,
    /// <summary>混合 IO（32 DI / 32 DO + 8 AI / 4 AO）</summary>
    MixedIo
}

/// <summary>
/// 虚拟设备目录：程序化生成"大型" IO 模块与伺服驱动器的 ESI 描述，
/// 并创建带行为模型的仿真从站。描述可以导出成标准 EtherCATInfo.xml，
/// 因此主站仍然走"读 ESI 文件 → 匹配 → 配置 PDO"的正常流程。
/// </summary>
public static class VirtualDeviceFactory
{
    public const uint VirtualVendorId = 0x00000ECA;
    public const string VirtualVendorName = "EtherCAT Virtual Devices";

    public const uint ProductBigServo = 0x00001001;
    public const uint ProductDualAxisServo = 0x00001002;
    public const uint ProductDigitalIo64 = 0x00002001;
    public const uint ProductMixedIo = 0x00002002;

    public const uint DefaultRevision = 0x00010000;

    public static uint GetProductCode(VirtualDeviceKind kind) => kind switch
    {
        VirtualDeviceKind.BigServo => ProductBigServo,
        VirtualDeviceKind.DualAxisServo => ProductDualAxisServo,
        VirtualDeviceKind.DigitalIo64 => ProductDigitalIo64,
        VirtualDeviceKind.MixedIo => ProductMixedIo,
        _ => 0
    };

    public static bool IsVirtualDevice(EsiDevice device) =>
        device != null && device.VendorId == VirtualVendorId && GetProductCodeFor(device.ProductCode) != null;

    private static VirtualDeviceKind? GetProductCodeFor(uint productCode) => productCode switch
    {
        ProductBigServo => VirtualDeviceKind.BigServo,
        ProductDualAxisServo => VirtualDeviceKind.DualAxisServo,
        ProductDigitalIo64 => VirtualDeviceKind.DigitalIo64,
        ProductMixedIo => VirtualDeviceKind.MixedIo,
        _ => null
    };

    /// <summary>全部内置虚拟设备的 ESI 描述</summary>
    public static List<EsiDevice> CreateAllDescriptions() =>
        Enum.GetValues<VirtualDeviceKind>().Select(CreateDescription).ToList();

    // ------------------------------------------------------------------ 描述

    public static EsiDevice CreateDescription(VirtualDeviceKind kind)
    {
        var device = new EsiDevice
        {
            VendorId = VirtualVendorId,
            VendorName = VirtualVendorName,
            ProductCode = GetProductCode(kind),
            RevisionNumber = DefaultRevision,
            SupportsDc = kind is VirtualDeviceKind.BigServo or VirtualDeviceKind.DualAxisServo,
            AssignActivate = 0x0300,
            CycleTimeSync0 = 1_000_000,
            ShiftTimeSync0 = 0
        };

        int rxBytes = 64, txBytes = 64;
        switch (kind)
        {
            case VirtualDeviceKind.BigServo:
                device.TypeText = "VSD-1A";
                device.Name = "虚拟单轴伺服驱动器 VSD-1A";
                device.RxPdos.Add(BuildPdo(0x1600, "RxPdo 伺服命令", 2,
                    (0x6040, 0, 16, "Controlword", "UINT"),
                    (0x607A, 0, 32, "Target position", "DINT"),
                    (0x60FF, 0, 32, "Target velocity", "DINT"),
                    (0x6071, 0, 16, "Target torque", "INT"),
                    (0x6060, 0, 8, "Modes of operation", "SINT"),
                    (0x60B8, 0, 16, "Touch probe function", "UINT")));
                device.TxPdos.Add(BuildPdo(0x1A00, "TxPdo 伺服状态", 3,
                    (0x6041, 0, 16, "Statusword", "UINT"),
                    (0x6064, 0, 32, "Position actual value", "DINT"),
                    (0x606C, 0, 32, "Velocity actual value", "DINT"),
                    (0x6077, 0, 16, "Torque actual value", "INT"),
                    (0x6061, 0, 8, "Modes of operation display", "SINT"),
                    (0x603F, 0, 16, "Error code", "UINT"),
                    (0x60FD, 0, 32, "Digital inputs", "UDINT"),
                    (0x60F4, 0, 32, "Following error actual value", "DINT"),
                    (0x60B9, 0, 16, "Touch probe status", "UINT"),
                    (0x60BA, 0, 32, "Touch probe pos1 pos value", "DINT")));
                break;

            case VirtualDeviceKind.DualAxisServo:
                device.TypeText = "VSD-2A";
                device.Name = "虚拟双轴伺服驱动器 VSD-2A";
                device.RxPdos.Add(BuildPdo(0x1600, "RxPdo 双轴命令", 2,
                    (0x6040, 0, 16, "Axis1 Controlword", "UINT"),
                    (0x607A, 0, 32, "Axis1 Target position", "DINT"),
                    (0x60FF, 0, 32, "Axis1 Target velocity", "DINT"),
                    (0x6060, 0, 8, "Axis1 Modes of operation", "SINT"),
                    (0x6840, 0, 16, "Axis2 Controlword", "UINT"),
                    (0x687A, 0, 32, "Axis2 Target position", "DINT"),
                    (0x68FF, 0, 32, "Axis2 Target velocity", "DINT"),
                    (0x6860, 0, 8, "Axis2 Modes of operation", "SINT")));
                device.TxPdos.Add(BuildPdo(0x1A00, "TxPdo 双轴状态", 3,
                    (0x6041, 0, 16, "Axis1 Statusword", "UINT"),
                    (0x6064, 0, 32, "Axis1 Position actual", "DINT"),
                    (0x606C, 0, 32, "Axis1 Velocity actual", "DINT"),
                    (0x6061, 0, 8, "Axis1 Mode display", "SINT"),
                    (0x603F, 0, 16, "Axis1 Error code", "UINT"),
                    (0x6841, 0, 16, "Axis2 Statusword", "UINT"),
                    (0x6864, 0, 32, "Axis2 Position actual", "DINT"),
                    (0x686C, 0, 32, "Axis2 Velocity actual", "DINT"),
                    (0x6861, 0, 8, "Axis2 Mode display", "SINT"),
                    (0x683F, 0, 16, "Axis2 Error code", "UINT")));
                break;

            case VirtualDeviceKind.DigitalIo64:
                device.TypeText = "VIO-6464";
                device.Name = "虚拟 64 点数字量 IO VIO-6464";
                device.SupportsDc = false;
                device.RxPdos.Add(BuildPdo(0x1600, "RxPdo 数字量输出", 2,
                    Enumerable.Range(1, 8).Select(i =>
                        ((ushort)0x7000, (byte)i, 8, $"Digital outputs byte {i}", "USINT")).ToArray()));
                device.TxPdos.Add(BuildPdo(0x1A00, "TxPdo 数字量输入", 3,
                    Enumerable.Range(1, 8).Select(i =>
                        ((ushort)0x6000, (byte)i, 8, $"Digital inputs byte {i}", "USINT")).ToArray()));
                break;

            case VirtualDeviceKind.MixedIo:
                device.TypeText = "VIO-MIX";
                device.Name = "虚拟混合 IO VIO-MIX（32DI/32DO + 8AI/4AO）";
                device.SupportsDc = false;
                device.RxPdos.Add(BuildPdo(0x1600, "RxPdo 输出", 2,
                    Enumerable.Range(1, 4).Select(i => ((ushort)0x7000, (byte)i, 8, $"Digital outputs {i}", "USINT"))
                        .Concat(Enumerable.Range(1, 4).Select(i => ((ushort)0x6411, (byte)i, 16, $"Analog output {i}", "INT")))
                        .ToArray()));
                device.TxPdos.Add(BuildPdo(0x1A00, "TxPdo 输入", 3,
                    Enumerable.Range(1, 4).Select(i => ((ushort)0x6000, (byte)i, 8, $"Digital inputs {i}", "USINT"))
                        .Concat(Enumerable.Range(1, 8).Select(i => ((ushort)0x6401, (byte)i, 16, $"Analog input {i}", "INT")))
                        .ToArray()));
                break;
        }

        rxBytes = Math.Max(8, device.RxPdos.Sum(p => p.ByteLength));
        txBytes = Math.Max(8, device.TxPdos.Sum(p => p.ByteLength));

        device.SyncManagers.Add(new EsiSyncManager { Index = 0, StartAddress = 0x1000, DefaultSize = 128, ControlByte = 0x26, Enable = true });
        device.SyncManagers.Add(new EsiSyncManager { Index = 1, StartAddress = 0x1080, DefaultSize = 128, ControlByte = 0x22, Enable = true });
        device.SyncManagers.Add(new EsiSyncManager { Index = 2, StartAddress = 0x1100, DefaultSize = rxBytes, ControlByte = 0x24, Enable = true });
        device.SyncManagers.Add(new EsiSyncManager { Index = 3, StartAddress = 0x1140, DefaultSize = txBytes, ControlByte = 0x20, Enable = true });

        return device;
    }

    private static EsiPdo BuildPdo(ushort index, string name, int sm,
        params (ushort Index, byte SubIndex, int BitLen, string Name, string DataType)[] entries)
    {
        var pdo = new EsiPdo { Index = index, Name = name, Fixed = true, Mandatory = true, SyncManager = sm };
        foreach (var e in entries)
        {
            pdo.Entries.Add(new EsiPdoEntry
            {
                Index = e.Index,
                SubIndex = e.SubIndex,
                BitLength = e.BitLen,
                Name = e.Name,
                DataType = e.DataType
            });
        }
        return pdo;
    }

    // ------------------------------------------------------------------ 创建从站

    /// <summary>按种类创建仿真从站（带对应行为模型）</summary>
    public static SimulatedSlave Create(VirtualDeviceKind kind, int position,
        VirtualDriveOptions? driveOptions = null, VirtualIoOptions? ioOptions = null)
    {
        var device = CreateDescription(kind);
        var slave = SimulatedSlave.FromEsi(device, position);
        AttachBehavior(slave, kind, driveOptions, ioOptions);
        return slave;
    }

    /// <summary>如果 ESI 描述属于内置虚拟设备，创建带行为模型的从站</summary>
    public static bool TryCreateFromEsi(EsiDevice device, int position, out SimulatedSlave? slave,
        VirtualDriveOptions? driveOptions = null, VirtualIoOptions? ioOptions = null)
    {
        var kind = device.VendorId == VirtualVendorId ? GetProductCodeFor(device.ProductCode) : null;
        if (kind == null)
        {
            slave = null;
            return false;
        }

        slave = SimulatedSlave.FromEsi(device, position);
        AttachBehavior(slave, kind.Value, driveOptions, ioOptions);
        return true;
    }

    private static void AttachBehavior(SimulatedSlave slave, VirtualDeviceKind kind,
        VirtualDriveOptions? driveOptions, VirtualIoOptions? ioOptions)
    {
        switch (kind)
        {
            case VirtualDeviceKind.BigServo:
            {
                var options = driveOptions?.Clone() ?? new VirtualDriveOptions { AxisCount = 1 };
                options.AxisCount = 1;
                EnsureDriveObjects(slave, 1);
                slave.Behavior = new VirtualDriveBehavior(options);
                break;
            }
            case VirtualDeviceKind.DualAxisServo:
            {
                var options = driveOptions?.Clone() ?? new VirtualDriveOptions { AxisCount = 2 };
                options.AxisCount = 2;
                EnsureDriveObjects(slave, 2);
                slave.Behavior = new VirtualDriveBehavior(options);
                break;
            }
            case VirtualDeviceKind.DigitalIo64:
            {
                var options = ioOptions?.Clone() ?? new VirtualIoOptions
                {
                    DigitalInputBytes = 8,
                    DigitalOutputBytes = 8,
                    AnalogInputCount = 0,
                    AnalogOutputCount = 0,
                    InputMode = VirtualDigitalInputMode.Loopback
                };
                slave.Behavior = new VirtualIoBehavior(options);
                break;
            }
            case VirtualDeviceKind.MixedIo:
            {
                var options = ioOptions?.Clone() ?? new VirtualIoOptions
                {
                    DigitalInputBytes = 4,
                    DigitalOutputBytes = 4,
                    AnalogInputCount = 8,
                    AnalogOutputCount = 4,
                    InputMode = VirtualDigitalInputMode.Loopback,
                    Wave = VirtualAnalogWave.Sine,
                    FrequencyHz = 0.5,
                    AmplitudeVolts = 5,
                    OffsetVolts = 5
                };
                slave.Behavior = new VirtualIoBehavior(options);
                break;
            }
        }
    }

    /// <summary>补齐驱动器常用的非 PDO 对象，便于 SDO 访问</summary>
    private static void EnsureDriveObjects(SimulatedSlave slave, int axisCount)
    {
        for (int axis = 1; axis <= axisCount; axis++)
        {
            int offset = VirtualDriveBehavior.AxisOffset(axis);
            void Add(ushort index, int size)
            {
                slave.EnsureObject((ushort)(index + offset), 0, size);
            }

            Add(0x6040, 2); Add(0x6041, 2);
            Add(0x6060, 1); Add(0x6061, 1);
            Add(0x6062, 4); Add(0x6064, 4); Add(0x606C, 4);
            Add(0x6067, 4); Add(0x6068, 2);
            Add(0x6071, 2); Add(0x6077, 2);
            Add(0x607A, 4); Add(0x60FF, 4);
            Add(0x603F, 2);
            Add(0x607C, 4);
            Add(0x6081, 4); Add(0x6083, 4); Add(0x6084, 4);
            Add(0x6098, 1);
            Add(0x60F4, 4); Add(0x60FD, 4);
            Add(0x60B8, 2); Add(0x60B9, 2); Add(0x60BA, 4); Add(0x60BB, 4);
            slave.EnsureObject((ushort)(0x6099 + offset), 1, 4);
            slave.EnsureObject((ushort)(0x6099 + offset), 2, 4);
        }
    }

    // ------------------------------------------------------------------ 网络

    /// <summary>按设备种类顺序建立一条仿真网络</summary>
    public static SimulatedLink CreateNetwork(IEnumerable<VirtualDeviceKind> kinds)
    {
        var link = new SimulatedLink();
        int position = 1;
        foreach (var kind in kinds)
            link.AddSlave(Create(kind, position++));
        return link;
    }

    /// <summary>典型"大型"演示网络：伺服 - 64 点 IO - 混合 IO - 双轴伺服</summary>
    public static SimulatedLink CreateLargeNetwork() => CreateNetwork(new[]
    {
        VirtualDeviceKind.BigServo,
        VirtualDeviceKind.DigitalIo64,
        VirtualDeviceKind.MixedIo,
        VirtualDeviceKind.DualAxisServo
    });

    // ------------------------------------------------------------------ 导出 ESI

    /// <summary>把一个 ESI 描述导出为 EtherCATInfo.xml 文本</summary>
    public static string ToEsiXml(EsiDevice device)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.AppendLine("<EtherCATInfo xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">");
        sb.AppendLine("  <Vendor>");
        sb.AppendLine($"    <Id>#x{device.VendorId:X8}</Id>");
        sb.AppendLine($"    <Name>{device.VendorName}</Name>");
        sb.AppendLine("  </Vendor>");
        sb.AppendLine("  <Descriptions>");
        sb.AppendLine("    <Devices>");
        sb.AppendLine("      <Device>");
        sb.AppendLine($"        <Type ProductCode=\"#x{device.ProductCode:X8}\" RevisionNo=\"#x{device.RevisionNumber:X8}\">{device.TypeText}</Type>");
        sb.AppendLine($"        <Name>{device.Name}</Name>");

        foreach (var sm in device.SyncManagers)
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"        <Sm StartAddress=\"#x{sm.StartAddress:X4}\" ControlByte=\"#x{sm.ControlByte:X2}\" " +
                $"DefaultSize=\"{sm.DefaultSize}\" MinSize=\"{sm.MinSize}\" MaxSize=\"{sm.MaxSize}\" " +
                $"Enable=\"{(sm.Enable ? 1 : 0)}\" />");
        }

        AppendPdo(sb, "RxPdo", device.RxPdos);
        AppendPdo(sb, "TxPdo", device.TxPdos);

        if (device.SupportsDc)
        {
            sb.AppendLine("        <Dc>");
            sb.AppendLine("          <OpMode>");
            sb.AppendLine($"            <Name>DC</Name>");
            sb.AppendLine($"            <AssignActivate>#x{device.AssignActivate:X4}</AssignActivate>");
            sb.AppendLine($"            <CycleTimeSync0>{device.CycleTimeSync0}</CycleTimeSync0>");
            sb.AppendLine($"            <ShiftTimeSync0>{device.ShiftTimeSync0}</ShiftTimeSync0>");
            sb.AppendLine("          </OpMode>");
            sb.AppendLine("        </Dc>");
        }

        sb.AppendLine("      </Device>");
        sb.AppendLine("    </Devices>");
        sb.AppendLine("  </Descriptions>");
        sb.AppendLine("</EtherCATInfo>");
        return sb.ToString();
    }

    private static void AppendPdo(StringBuilder sb, string elementName, List<EsiPdo> pdos)
    {
        foreach (var pdo in pdos)
        {
            sb.AppendLine($"        <{elementName} Fixed=\"1\" Mandatory=\"1\" Sm=\"{pdo.SyncManager}\">");
            sb.AppendLine($"          <Index>#x{pdo.Index:X4}</Index>");
            sb.AppendLine($"          <Name>{pdo.Name}</Name>");
            foreach (var e in pdo.Entries)
            {
                sb.AppendLine("          <Entry>");
                sb.AppendLine($"            <Index>#x{e.Index:X4}</Index>");
                sb.AppendLine($"            <SubIndex>{e.SubIndex}</SubIndex>");
                sb.AppendLine($"            <BitLen>{e.BitLength}</BitLen>");
                sb.AppendLine($"            <Name>{e.Name}</Name>");
                sb.AppendLine($"            <DataType>{e.DataType}</DataType>");
                sb.AppendLine("          </Entry>");
            }
            sb.AppendLine($"        </{elementName}>");
        }
    }

    /// <summary>把内置虚拟设备的 ESI 文件写到目录（不存在时创建）</summary>
    public static int ExportEsiFiles(string directory, bool overwrite = false)
    {
        Directory.CreateDirectory(directory);
        int count = 0;
        foreach (var device in CreateAllDescriptions())
        {
            string path = Path.Combine(directory, $"{device.TypeText}.xml");
            if (File.Exists(path) && !overwrite)
                continue;
            File.WriteAllText(path, ToEsiXml(device), new System.Text.UTF8Encoding(false));
            count++;
        }
        return count;
    }
}

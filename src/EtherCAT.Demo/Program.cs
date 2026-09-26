using EtherCAT;
using EtherCAT.Devices;
using EtherCAT.Esi;
using EtherCAT.Link.Simulated;
using EtherCAT.Master;

// EtherCAT 主站端到端冒烟测试 / 虚拟大模块演示
//
//   dotnet run --project src/EtherCAT.Demo                 标准冒烟测试（伺服 + IO + 伺服）
//   dotnet run --project src/EtherCAT.Demo -- --virtual    大型虚拟设备演示（64点IO / 混合IO / 单轴 / 双轴伺服）
//   dotnet run --project src/EtherCAT.Demo -- --export-esi 导出内置虚拟设备的 ESI 描述文件
//
// 用真实硬件时只需把 SimulatedLink 换成 NpcapLink（见 README）。

var commandArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();
string esiDirectory = FindEsiDirectory();
Console.WriteLine($"ESI 目录：{esiDirectory}");

if (commandArgs.Contains("--export-esi"))
{
    int written = VirtualDeviceFactory.ExportEsiFiles(esiDirectory, overwrite: true);
    Console.WriteLine($"已导出 {written} 个虚拟设备 ESI 描述到 {esiDirectory}");
    return 0;
}

var esi = new EsiDatabase();
int loaded = esi.LoadDirectory(esiDirectory, true, new DelegateLog(Console.WriteLine));
Console.WriteLine($"加载 ESI 设备数：{loaded}");
if (loaded == 0)
{
    Console.Error.WriteLine("未找到 ESI 文件，无法继续");
    return 1;
}

return commandArgs.Contains("--virtual")
    ? RunVirtualDeviceDemo(esi)
    : RunStandardSmokeTest(esi);

// ------------------------------------------------------------------ 标准冒烟测试

static int RunStandardSmokeTest(EsiDatabase esi)
{
    // 构造仿真网络：伺服 - IO - 伺服
    var servo = esi.Devices.First(d => d.ProductCode == 0x00000001);
    var io = esi.Devices.First(d => d.ProductCode == 0x00000002);
    using var link = SimulatedLink.CreateFromEsi(new[] { servo, io, servo });
    link.Open("sim0");

    using var master = new EthercatMaster(link, esi, new DelegateLog(Console.WriteLine));

    int count = master.Scan();
    Console.WriteLine($"扫描到从站数：{count}");
    if (count == 0)
        return 1;

    foreach (var slave in master.Slaves)
    {
        Console.WriteLine($"  #{slave.SlaveIndex} {slave.ProductName,-28} " +
                          $"Vendor=0x{slave.VendorId:X8} Product=0x{slave.ProductCode:X8} " +
                          $"Rev=0x{slave.RevisionNumber:X8} 映射={slave.PdoSource} " +
                          $"{(slave.IsDrive ? "[伺服]" : "")}{(slave.IsIoModule ? "[IO]" : "")}");
    }

    if (!master.Configure())
    {
        Console.Error.WriteLine("配置失败，未能进入 OP");
        return 1;
    }

    Console.WriteLine($"镜像大小 {master.Image.Size} 字节，期望 WKC = {master.ExpectedWorkingCounter}");

    master.CycleTimeUs = 1000;
    master.StartCyclic();

    // ---- 伺服控制 ----
    var drives = DeviceFactory.FindDrives(master);
    Console.WriteLine($"发现伺服驱动器：{drives.Count}");
    var drive = drives.First();

    Console.WriteLine($"初始状态：{drive.StateText} (0x{drive.Statusword:X4})");
    if (!drive.Enable())
    {
        Console.Error.WriteLine("伺服使能失败");
        return 1;
    }
    Console.WriteLine($"使能后状态：{drive.StateText} (0x{drive.Statusword:X4})");

    drive.Mode = CiA402Mode.ProfileVelocity;
    Thread.Sleep(20);
    Console.WriteLine($"运行模式：{drive.Mode}");

    drive.SetTargetVelocity(100000);
    Thread.Sleep(500);
    Console.WriteLine($"速度模式下：位置={drive.PositionActual} 速度={drive.VelocityActual}");

    drive.Mode = CiA402Mode.ProfilePosition;
    drive.SetTargetPosition(500000);
    Thread.Sleep(800);
    Console.WriteLine($"位置模式下：位置={drive.PositionActual} 目标={drive.TargetPosition} 到位={drive.TargetReached}");

    drive.Disable();
    Thread.Sleep(50);
    Console.WriteLine($"禁用后状态：{drive.StateText}");

    // ---- IO 读写 ----
    var ioModules = DeviceFactory.FindIoModules(master);
    Console.WriteLine($"发现 IO 模块：{ioModules.Count}");
    var ioModule = ioModules.First();

    ioModule.OutputWord = 0x00FF;
    Thread.Sleep(50);
    Console.WriteLine($"写入 DO = 0x{ioModule.OutputWord:X4}");

    var simulatedIo = link.Slaves.First(s => s.ProductCode == 0x00000002);
    simulatedIo.DigitalInputs = 0xA5A5;   // 模拟外部输入变化
    Thread.Sleep(50);
    Console.WriteLine($"读取 DI = 0x{ioModule.InputWord:X4}");

    // ---- 周期统计 ----
    int wkcErrors = master.WorkingCounterErrors;
    Console.WriteLine($"周期通信：WKC = {master.LastWorkingCounter} / {master.ExpectedWorkingCounter}，错误计数 {wkcErrors}");

    master.StopCyclic();

    bool ok = master.LastWorkingCounter == master.ExpectedWorkingCounter
              && wkcErrors == 0
              && drive.PositionActual != 0
              && ioModule.InputWord == 0xA5A5
              && ioModule.OutputWord == 0x00FF;

    Console.WriteLine(ok ? "== 冒烟测试通过 ==" : "== 冒烟测试失败 ==");
    return ok ? 0 : 1;
}

// ------------------------------------------------------------------ 虚拟大模块演示

static int RunVirtualDeviceDemo(EsiDatabase esi)
{
    // 1) 取出 ESI 中属于内置虚拟设备的描述（等于"按描述文件组态"后再接上仿真行为）
    var virtualDevices = esi.Devices.Where(d => VirtualDeviceFactory.IsVirtualDevice(d)).ToList();
    if (virtualDevices.Count == 0)
    {
        Console.Error.WriteLine($"ESI 目录中没有虚拟设备描述，请先执行：dotnet run --project src/EtherCAT.Demo -- --export-esi");
        return 1;
    }

    // 按 伺服 → 64点IO → 混合IO → 双轴伺服 排序组网
    var order = new[]
    {
        VirtualDeviceFactory.ProductBigServo,
        VirtualDeviceFactory.ProductDigitalIo64,
        VirtualDeviceFactory.ProductMixedIo,
        VirtualDeviceFactory.ProductDualAxisServo
    };
    var devices = order
        .Select(code => virtualDevices.FirstOrDefault(d => d.ProductCode == code))
        .Where(d => d != null)
        .Select(d => d!)
        .ToList();

    Console.WriteLine($"虚拟网络：{string.Join(" → ", devices.Select(d => d.DisplayName))}");

    var driveOptions = new VirtualDriveOptions
    {
        EncoderResolution = 10000,
        MaxVelocity = 800_000,
        MaxAcceleration = 3_000_000,
        PositionLoopGain = 150,
        PositiveLimit = 1_500_000,
        NegativeLimit = -1_500_000,
        HomeSwitchWindow = 2000,
        PositionWindow = 500
    };

    using var link = SimulatedLink.CreateFromEsi(devices, 1, driveOptions);
    link.Open("sim0");

    using var master = new EthercatMaster(link, esi, new DelegateLog(Console.WriteLine));
    int count = master.Scan();
    Console.WriteLine($"扫描到从站数：{count}");

    foreach (var slave in master.Slaves)
    {
        Console.WriteLine($"  #{slave.SlaveIndex} {slave.ProductName,-32} " +
                          $"输出 {slave.OutputByteLength,3}B 输入 {slave.InputByteLength,3}B " +
                          $"映射={slave.PdoSource} {(slave.IsDrive ? "[伺服]" : "")}{(slave.IsIoModule ? "[IO]" : "")}");
    }

    if (!master.Configure())
    {
        Console.Error.WriteLine("配置失败，未能进入 OP");
        return 1;
    }

    Console.WriteLine($"镜像大小 {master.Image.Size} 字节，期望 WKC = {master.ExpectedWorkingCounter}");
    master.CycleTimeUs = 1000;
    master.StartCyclic();

    bool ok = true;

    // ---------------- 伺服：多轴使能 + 各模式运动 ----------------
    var drives = DeviceFactory.FindDrives(master);
    Console.WriteLine($"\n[伺服] 发现 {drives.Count} 个轴：{string.Join("、", drives.Select(d => d.DisplayName))}");

    foreach (var d in drives)
    {
        if (!d.Enable())
        {
            Console.Error.WriteLine($"  {d.DisplayName} 使能失败 (0x{d.Statusword:X4})");
            ok = false;
        }
        else
        {
            Console.WriteLine($"  {d.DisplayName} 使能成功：{d.StateText} (0x{d.Statusword:X4})");
        }
    }

    var axis1 = drives.First();

    // 速度模式
    axis1.Mode = CiA402Mode.ProfileVelocity;
    Thread.Sleep(30);
    axis1.SetTargetVelocity(300_000);
    Thread.Sleep(400);
    Console.WriteLine($"  PV 模式：位置={axis1.PositionActual} 速度={axis1.VelocityActual} 模式显示={axis1.Mode}");
    ok &= Math.Abs(axis1.VelocityActual) > 1000;

    // 回零
    axis1.Mode = CiA402Mode.Homing;
    Thread.Sleep(30);
    axis1.SetTargetVelocity(0);
    Thread.Sleep(50);
    Console.WriteLine($"  回零前位置 = {axis1.PositionActual}");
    axis1.StartHoming(19);
    bool homed = WaitFor(() => axis1.HomingAttained, 5000);
    Console.WriteLine($"  回零{(homed ? "完成" : "未完成")}：位置={axis1.PositionActual} 状态字=0x{axis1.Statusword:X4}");
    ok &= homed;

    // 周期同步位置模式（CSP）
    axis1.Mode = CiA402Mode.CyclicSynchronousPosition;
    Thread.Sleep(30);
    for (int i = 1; i <= 20; i++)
    {
        axis1.SetTargetPosition(i * 5000);
        Thread.Sleep(20);
    }
    Console.WriteLine($"  CSP 模式：目标={axis1.TargetPosition} 实际={axis1.PositionActual} " +
                      $"速度={axis1.VelocityActual} 到位={axis1.TargetReached}");
    ok &= Math.Abs(axis1.PositionActual - axis1.TargetPosition) < 20000;

    // 故障注入与复位
    var bigServo = link.Slaves.Select(s => s.Behavior).OfType<VirtualDriveBehavior>().First();
    bigServo.TriggerFault(0, 0x8611);
    Thread.Sleep(60);
    bool sawFault = axis1.Fault;
    axis1.FaultReset();
    Thread.Sleep(60);
    bool recovered = !axis1.Fault || axis1.Enable(2000);
    Console.WriteLine($"  故障注入 0x8611 → Fault={sawFault}，复位后状态={axis1.StateText}");
    ok &= sawFault && recovered;

    axis1.Disable();
    Thread.Sleep(50);

    // ---------------- 64 点数字量 IO ----------------
    var modules = DeviceFactory.FindIoModules(master);
    Console.WriteLine($"\n[IO] 发现 {modules.Count} 个模块");
    foreach (var m in modules)
        Console.WriteLine($"  {m}");

    var bigIo = modules.FirstOrDefault(m => m.Outputs.Count >= 64);
    if (bigIo != null)
    {
        ulong pattern = 0xF0F0_F0F0_0F0F_0F0F;
        bigIo.OutputWord = pattern;
        Thread.Sleep(80);
        ulong readback = bigIo.InputWord;   // 虚拟模块默认处于回环模式
        Console.WriteLine($"  64 点 IO：写 0x{pattern:X16} → 读 0x{readback:X16}（回环）");
        ok &= readback == pattern;
    }
    else
    {
        Console.Error.WriteLine("  未找到 64 点 IO 模块");
        ok = false;
    }

    // ---------------- 混合 IO：模拟量 ----------------
    var mixed = modules.FirstOrDefault(m => m.HasAnalog);
    if (mixed != null)
    {
        Console.WriteLine($"  模拟量输入 {mixed.AnalogInputs.Count} 路 / 输出 {mixed.AnalogOutputs.Count} 路");

        var samples = new List<double>();
        for (int i = 0; i < 5; i++)
        {
            Thread.Sleep(120);
            samples.Add(mixed.AnalogInputs[0].Volts);
        }
        Console.WriteLine($"  AI0 采样（V）：{string.Join(", ", samples.Select(v => v.ToString("F2")))}");
        bool varying = samples.Max() - samples.Min() > 0.05;
        ok &= varying;

        mixed.AnalogOutputs[0].Volts = 7.5;
        mixed.AnalogOutputs[1].Volts = -3.0;
        Thread.Sleep(600);   // 斜率限制 20 V/s，需要爬升时间
        ushort raw0 = master.Coe.ReadUInt16(mixed.SlaveIndex, 0x6411, 1);
        ushort raw1 = master.Coe.ReadUInt16(mixed.SlaveIndex, 0x6411, 2);
        double v0 = unchecked((short)raw0) / 32767.0 * 10.0;
        double v1 = unchecked((short)raw1) / 32767.0 * 10.0;
        Console.WriteLine($"  AO 写入 7.50V / -3.00V → 回读 {v0:F2}V / {v1:F2}V（含斜率限制）");
        ok &= Math.Abs(v0 - 7.5) < 0.5 && Math.Abs(v1 + 3.0) < 0.5;

        // 数字量部分仍然可用
        mixed.OutputWord = 0x0000_00FF;
        Thread.Sleep(80);
        Console.WriteLine($"  混合 IO 数字量：写 0x{mixed.OutputWord:X8} → 读 0x{mixed.InputWord:X8}");
        ok &= mixed.InputWord == mixed.OutputWord;
    }
    else
    {
        Console.Error.WriteLine("  未找到带模拟量的 IO 模块");
        ok = false;
    }

    // ---------------- 周期统计 ----------------
    int wkcErrors = master.WorkingCounterErrors;
    Console.WriteLine($"\n周期通信：WKC = {master.LastWorkingCounter} / {master.ExpectedWorkingCounter}，错误计数 {wkcErrors}");
    ok &= master.LastWorkingCounter == master.ExpectedWorkingCounter && wkcErrors == 0;

    master.StopCyclic();
    Console.WriteLine(ok ? "== 虚拟设备演示通过 ==" : "== 虚拟设备演示失败 ==");
    return ok ? 0 : 1;
}

static bool WaitFor(Func<bool> predicate, int timeoutMs)
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    while (sw.ElapsedMilliseconds < timeoutMs)
    {
        if (predicate())
            return true;
        Thread.Sleep(10);
    }
    return predicate();
}

static string FindEsiDirectory()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory != null)
    {
        var candidate = Path.Combine(directory.FullName, "esi");
        if (Directory.Exists(candidate))
            return candidate;
        directory = directory.Parent;
    }
    return Path.Combine(AppContext.BaseDirectory, "esi");
}

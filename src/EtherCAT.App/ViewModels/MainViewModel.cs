using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using Avalonia.Threading;
using EtherCAT.Devices;
using EtherCAT.Esi;
using EtherCAT.Link;
using EtherCAT.Link.Simulated;
using EtherCAT.Master;

namespace EtherCAT.App.ViewModels;

public sealed class SlaveViewModel : ViewModelBase
{
    private readonly SlaveInfo _slave;

    public SlaveViewModel(SlaveInfo slave) => _slave = slave;

    public SlaveInfo Slave => _slave;

    public int Index => _slave.SlaveIndex;
    public string Name => _slave.ProductName;
    public string Vendor => $"0x{_slave.VendorId:X8}";
    public string ProductCode => $"0x{_slave.ProductCode:X8}";
    public string Revision => $"0x{_slave.RevisionNumber:X8}";
    public string Serial => $"0x{_slave.SerialNumber:X8}";
    public string Mailbox => _slave.HasMailbox ? "CoE" : "-";
    public string Pdo => _slave.PdoSource.ToString();
    public string EsiFile => _slave.Esi != null ? Path.GetFileName(_slave.Esi.SourceFile) : "未匹配";
    public string Kind => _slave.IsDrive ? "伺服" : _slave.IsIoModule ? "IO" : "其它";

    public string StateText => _slave.AlStatusText;
    public string Mapping => $"输出 {_slave.OutputBitSize} bit / 输入 {_slave.InputBitSize} bit";

    public void Refresh()
    {
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(Mapping));
        OnPropertyChanged(nameof(Pdo));
    }
}

public sealed class PdoVariableViewModel : ViewModelBase
{
    private readonly PdoVariable _variable;

    public PdoVariableViewModel(PdoVariable variable, int slaveIndex)
    {
        _variable = variable;
        SlaveIndex = slaveIndex;
    }

    public int SlaveIndex { get; }
    public string Direction => _variable.IsOutput ? "输出" : "输入";
    public string Index => $"0x{_variable.Index:X4}";
    public string SubIndex => _variable.SubIndex == 0 ? "-" : _variable.SubIndex.ToString();
    public string Name => _variable.DisplayName;
    public string Bits => _variable.BitLength.ToString();

    public string ValueText
    {
        get
        {
            ulong raw = _variable.RawValue;
            return _variable.BitLength <= 8
                ? $"0x{raw:X2}"
                : _variable.BitLength <= 16
                    ? $"0x{raw:X4} / {(long)raw}"
                    : $"0x{raw:X8} / {unchecked((int)(uint)raw)}";
        }
    }

    public void Refresh() => OnPropertyChanged(nameof(ValueText));
}

public sealed class IoChannelViewModel : ViewModelBase
{
    private readonly IoChannel _channel;

    public IoChannelViewModel(IoChannel channel) => _channel = channel;

    public string Name => _channel.Name;

    public bool Value
    {
        get => _channel.Value;
        set
        {
            _channel.Value = value;
            Refresh();
        }
    }

    public void Refresh() => OnPropertyChanged(nameof(Value));
}

/// <summary>模拟量通道视图模型（AI 只读显示，AO 可设定电压）</summary>
public sealed class AnalogChannelViewModel : ViewModelBase
{
    private readonly AnalogChannel _channel;

    public AnalogChannelViewModel(AnalogChannel channel) => _channel = channel;

    public string Name => _channel.Name;
    public bool IsOutput => _channel.IsOutput;

    public double Volts
    {
        get => _channel.Volts;
        set
        {
            _channel.Volts = value;
            Refresh();
        }
    }

    public string RawText => _channel.Raw.ToString();

    public void Refresh()
    {
        OnPropertyChanged(nameof(Volts));
        OnPropertyChanged(nameof(RawText));
    }
}

public sealed class IoModuleViewModel : ViewModelBase
{
    private readonly IoModule _module;

    public IoModuleViewModel(IoModule module)
    {
        _module = module;
        foreach (var input in module.Inputs)
            Inputs.Add(new IoChannelViewModel(input));
        foreach (var output in module.Outputs)
            Outputs.Add(new IoChannelViewModel(output));
        foreach (var input in module.AnalogInputs)
            AnalogInputs.Add(new AnalogChannelViewModel(input));
        foreach (var output in module.AnalogOutputs)
            AnalogOutputs.Add(new AnalogChannelViewModel(output));

        AllOutputsOnCommand = new RelayCommand(() =>
        {
            foreach (var o in Outputs) o.Value = true;
        });
        AllOutputsOffCommand = new RelayCommand(() =>
        {
            foreach (var o in Outputs) o.Value = false;
        });
    }

    public IoModule Module => _module;
    public string DisplayName => _module.ToString();
    public ObservableCollection<IoChannelViewModel> Inputs { get; } = new();
    public ObservableCollection<IoChannelViewModel> Outputs { get; } = new();
    public ObservableCollection<AnalogChannelViewModel> AnalogInputs { get; } = new();
    public ObservableCollection<AnalogChannelViewModel> AnalogOutputs { get; } = new();

    public bool HasAnalog => _module.HasAnalog;
    public string AnalogSummary => $"{AnalogInputs.Count} AI / {AnalogOutputs.Count} AO";

    public ICommand AllOutputsOnCommand { get; }
    public ICommand AllOutputsOffCommand { get; }

    public string InputWordText => $"0x{_module.InputWord:X4}";
    public string OutputWordText => $"0x{_module.OutputWord:X4}";

    public void Refresh()
    {
        foreach (var input in Inputs)
            input.Refresh();
        foreach (var channel in AnalogInputs)
            channel.Refresh();
        foreach (var channel in AnalogOutputs)
            channel.Refresh();
        OnPropertyChanged(nameof(InputWordText));
        OnPropertyChanged(nameof(OutputWordText));
    }
}

public sealed class DriveViewModel : ViewModelBase
{
    private readonly CiA402Drive _drive;
    private int _targetVelocity = 100000;
    private int _targetPosition;
    private CiA402Mode _selectedMode = CiA402Mode.ProfileVelocity;

    public DriveViewModel(CiA402Drive drive)
    {
        _drive = drive;
        EnableCommand = new RelayCommand(() => Run(() => _drive.Enable()));
        DisableCommand = new RelayCommand(() => Run(_drive.Disable));
        FaultResetCommand = new RelayCommand(() => Run(_drive.FaultReset));
        StopCommand = new RelayCommand(() =>
        {
            _drive.SetTargetVelocity(0);
            _drive.Halt(true);
        });
        ApplyVelocityCommand = new RelayCommand(() =>
        {
            SelectedMode = CiA402Mode.ProfileVelocity;
            _drive.SetTargetVelocity(_targetVelocity);
            _drive.Halt(false);
        });
        MoveAbsoluteCommand = new RelayCommand(() =>
        {
            SelectedMode = CiA402Mode.ProfilePosition;
            _drive.SetTargetPosition(_targetPosition);
            _drive.Halt(false);
        });
        JogForwardCommand = new RelayCommand(() => Jog(Math.Abs(_targetVelocity)));
        JogBackwardCommand = new RelayCommand(() => Jog(-Math.Abs(_targetVelocity)));
        HomeCommand = new RelayCommand(() =>
        {
            SelectedMode = CiA402Mode.Homing;
            _drive.StartHoming(17);
        });
    }

    private void Run(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            OnPropertyChanged(nameof(LastError));
        }
    }

    private void Jog(int velocity)
    {
        SelectedMode = CiA402Mode.ProfileVelocity;
        _drive.SetTargetVelocity(velocity);
        _drive.Halt(false);
    }

    public CiA402Drive Drive => _drive;
    public string DisplayName => _drive.ToString();
    public string LastError { get; private set; } = "";

    public ICommand EnableCommand { get; }
    public ICommand DisableCommand { get; }
    public ICommand FaultResetCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand ApplyVelocityCommand { get; }
    public ICommand MoveAbsoluteCommand { get; }
    public ICommand JogForwardCommand { get; }
    public ICommand JogBackwardCommand { get; }
    public ICommand HomeCommand { get; }

    public CiA402Mode[] Modes { get; } =
    {
        CiA402Mode.ProfilePosition,
        CiA402Mode.ProfileVelocity,
        CiA402Mode.Velocity,
        CiA402Mode.Homing,
        CiA402Mode.CyclicSynchronousPosition,
        CiA402Mode.CyclicSynchronousVelocity
    };

    public CiA402Mode SelectedMode
    {
        get => _selectedMode;
        set
        {
            if (!SetProperty(ref _selectedMode, value))
                return;
            try
            {
                _drive.Mode = value;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }
        }
    }

    public int TargetVelocity
    {
        get => _targetVelocity;
        set => SetProperty(ref _targetVelocity, value);
    }

    public int TargetPosition
    {
        get => _targetPosition;
        set => SetProperty(ref _targetPosition, value);
    }

    public string StateText => _drive.StateText;
    public string StatuswordText => $"0x{_drive.Statusword:X4}";
    public string PositionText => _drive.PositionActual.ToString("N0");
    public string VelocityText => _drive.VelocityActual.ToString("N0");
    public string TorqueText => _drive.TorqueActual.ToString();
    public string ErrorText => $"0x{_drive.ErrorCode:X4}";
    public string ModeText => _drive.Mode.ToString();
    public bool IsEnabled => _drive.OperationEnabled;

    public void Refresh()
    {
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(StatuswordText));
        OnPropertyChanged(nameof(PositionText));
        OnPropertyChanged(nameof(VelocityText));
        OnPropertyChanged(nameof(TorqueText));
        OnPropertyChanged(nameof(ErrorText));
        OnPropertyChanged(nameof(ModeText));
        OnPropertyChanged(nameof(IsEnabled));
    }
}

public sealed class MainViewModel : ViewModelBase, IDisposable
{
    private readonly EsiDatabase _esi = new();
    private readonly DispatcherTimer _refreshTimer;
    private IEthercatLink? _link;
    private EthercatMaster? _master;

    public MainViewModel()
    {
        ConnectCommand = new RelayCommand(Connect, () => !IsConnected);
        DisconnectCommand = new RelayCommand(Disconnect, () => IsConnected);
        ScanCommand = new RelayCommand(Scan, () => IsConnected);
        ConfigureCommand = new RelayCommand(Configure, () => IsConnected && HasSlaves);
        StartCyclicCommand = new RelayCommand(StartCyclic, () => IsConnected && IsOperational && !IsCyclicRunning);
        StopCyclicCommand = new RelayCommand(StopCyclic, () => IsCyclicRunning);
        ReloadEsiCommand = new RelayCommand(LoadEsi);
        ClearLogCommand = new RelayCommand(() => LogLines.Clear());

        LoadEsi();
        RefreshAdapters();

        _refreshTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Normal, (_, _) => Refresh());
        _refreshTimer.Start();
    }

    // ------------------------------------------------------------------ 连接

    public ObservableCollection<NetworkAdapterInfo> Adapters { get; } = new();

    private NetworkAdapterInfo? _selectedAdapter;
    public NetworkAdapterInfo? SelectedAdapter
    {
        get => _selectedAdapter;
        set => SetProperty(ref _selectedAdapter, value);
    }

    private bool _useSimulation = true;
    public bool UseSimulation
    {
        get => _useSimulation;
        set
        {
            if (SetProperty(ref _useSimulation, value))
            {
                RefreshAdapters();
                OnPropertyChanged(nameof(IsSimulationConfigVisible));
            }
        }
    }

    /// <summary>仿真网络拓扑预设</summary>
    public ObservableCollection<string> SimulationPresets { get; } = new()
    {
        StandardPreset,
        LargeVirtualPreset
    };

    public const string StandardPreset = "标准网络（伺服 + IO + 伺服）";
    public const string LargeVirtualPreset = "大型虚拟网络（64点IO / 混合IO / 单轴 / 双轴伺服）";

    private string _selectedPreset = StandardPreset;
    public string SelectedPreset
    {
        get => _selectedPreset;
        set => SetProperty(ref _selectedPreset, value);
    }

    public bool IsSimulationConfigVisible => UseSimulation;

    private bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (SetProperty(ref _isConnected, value))
                RaiseCommandStates();
        }
    }

    private bool _isOperational;
    public bool IsOperational
    {
        get => _isOperational;
        private set
        {
            if (SetProperty(ref _isOperational, value))
                RaiseCommandStates();
        }
    }

    private bool _isCyclicRunning;
    public bool IsCyclicRunning
    {
        get => _isCyclicRunning;
        private set
        {
            if (SetProperty(ref _isCyclicRunning, value))
                RaiseCommandStates();
        }
    }

    private int _cycleTimeUs = 1000;
    public int CycleTimeUs
    {
        get => _cycleTimeUs;
        set
        {
            if (SetProperty(ref _cycleTimeUs, value) && _master != null)
                _master.CycleTimeUs = value;
        }
    }

    private bool _useDistributedClock;
    public bool UseDistributedClock
    {
        get => _useDistributedClock;
        set => SetProperty(ref _useDistributedClock, value);
    }

    public bool HasSlaves => Slaves.Count > 0;

    public ICommand ConnectCommand { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand ScanCommand { get; }
    public ICommand ConfigureCommand { get; }
    public ICommand StartCyclicCommand { get; }
    public ICommand StopCyclicCommand { get; }
    public ICommand ReloadEsiCommand { get; }
    public ICommand ClearLogCommand { get; }

    public ObservableCollection<SlaveViewModel> Slaves { get; } = new();
    public ObservableCollection<DriveViewModel> Drives { get; } = new();
    public ObservableCollection<AcsDriveViewModel> AcsDrives { get; } = new();
    public ObservableCollection<IoModuleViewModel> IoModules { get; } = new();
    public ObservableCollection<PdoVariableViewModel> PdoVariables { get; } = new();
    public ObservableCollection<string> LogLines { get; } = new();

    /// <summary>是否有伺服驱动器（用于页签空状态提示）</summary>
    public bool HasDrives => Drives.Count > 0;
    /// <summary>是否有 IO 模块</summary>
    public bool HasIoModules => IoModules.Count > 0;
    /// <summary>是否有 ACS 驱动器（标准 CiA402 伺服，用于“ACS 驱动器”页签）</summary>
    public bool HasAcsDrives => AcsDrives.Count > 0;
    /// <summary>是否已加载 PDO 过程数据变量</summary>
    public bool HasPdoData => PdoVariables.Count > 0;

    private SlaveViewModel? _selectedSlave;
    public SlaveViewModel? SelectedSlave
    {
        get => _selectedSlave;
        set => SetProperty(ref _selectedSlave, value);
    }

    private string _statusText = "未连接";
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    private string _wkcText = "-";
    public string WkcText
    {
        get => _wkcText;
        private set => SetProperty(ref _wkcText, value);
    }

    public string EsiSummary => $"ESI 设备：{_esi.Count}";

    // ------------------------------------------------------------------ 操作

    private void RefreshAdapters()
    {
        Adapters.Clear();
        if (UseSimulation)
        {
            Adapters.Add(new NetworkAdapterInfo { Name = "sim0", Description = "仿真网络（虚拟从站）" });
            SelectedAdapter = Adapters.FirstOrDefault();
            return;
        }

        try
        {
            using var probe = new NpcapLink();
            foreach (var adapter in probe.EnumerateAdapters())
                Adapters.Add(adapter);
        }
        catch (Exception ex)
        {
            Log($"枚举网卡失败：{ex.Message}（请安装 Npcap）");
        }

        SelectedAdapter = Adapters.FirstOrDefault();
    }

    private void Connect()
    {
        try
        {
            if (UseSimulation)
            {
                _link = BuildSimulatedNetwork();
                _link.Open("sim0");
            }
            else
            {
                if (SelectedAdapter == null)
                {
                    Log("请选择网卡");
                    return;
                }
                var npcap = new NpcapLink();
                npcap.Open(SelectedAdapter.Name);
                _link = npcap;
            }

            _master = new EthercatMaster(_link, _esi, new DelegateLog(Log));
            _master.CycleTimeUs = CycleTimeUs;
            IsConnected = true;
            StatusText = UseSimulation ? "已连接（仿真）" : $"已连接 {SelectedAdapter?.Name}";
            Log(StatusText);
        }
        catch (Exception ex)
        {
            Log($"连接失败：{ex.Message}");
            StatusText = "连接失败";
        }
    }

    private IEthercatLink BuildSimulatedNetwork()
    {
        return SelectedPreset == LargeVirtualPreset
            ? BuildLargeVirtualNetwork()
            : BuildStandardNetwork();
    }

    private IEthercatLink BuildStandardNetwork()
    {
        var devices = new List<EsiDevice>();
        var drive = _esi.Devices.FirstOrDefault(d =>
            d.RxPdos.Concat(d.TxPdos).SelectMany(p => p.Entries).Any(e => e.Index == 0x6040));
        var io = _esi.Devices.FirstOrDefault(d =>
            d.RxPdos.Concat(d.TxPdos).SelectMany(p => p.Entries).Any(e => e.Index == 0x7000 || e.Index == 0x6000));

        if (drive != null) devices.Add(drive);
        if (io != null) devices.Add(io);
        if (drive != null) devices.Add(drive);
        if (devices.Count == 0)
            devices.AddRange(_esi.Devices.Take(1));

        Log($"仿真网络：{string.Join(" → ", devices.Select(d => d.DisplayName))}");
        return SimulatedLink.CreateFromEsi(devices);
    }

    /// <summary>用内置虚拟设备（大 IO 模块 / 大驱动器）组一条网络</summary>
    private IEthercatLink BuildLargeVirtualNetwork()
    {
        var virtualDevices = _esi.Devices.Where(d => VirtualDeviceFactory.IsVirtualDevice(d)).ToList();
        if (virtualDevices.Count == 0)
        {
            Log("ESI 目录中没有虚拟设备描述，自动导出内置虚拟设备描述文件…");
            try
            {
                VirtualDeviceFactory.ExportEsiFiles(EsiDirectory);
                LoadEsi();
                virtualDevices = _esi.Devices.Where(d => VirtualDeviceFactory.IsVirtualDevice(d)).ToList();
            }
            catch (Exception ex)
            {
                Log($"导出虚拟设备 ESI 失败：{ex.Message}");
            }
        }

        if (virtualDevices.Count == 0)
        {
            Log("没有可用的虚拟设备，回退到标准网络");
            return BuildStandardNetwork();
        }

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

        Log($"仿真网络：{string.Join(" → ", devices.Select(d => d.DisplayName))}");
        return SimulatedLink.CreateFromEsi(devices, 1, driveOptions);
    }

    private void Disconnect()
    {
        try
        {
            _master?.StopCyclic();
            _master?.Dispose();
        }
        catch { /* 忽略关闭异常 */ }

        try
        {
            _link?.Dispose();
        }
        catch { /* 忽略 */ }

        _master = null;
        _link = null;
        IsConnected = false;
        IsOperational = false;
        IsCyclicRunning = false;
        Slaves.Clear();
        Drives.Clear();
        AcsDrives.Clear();
        IoModules.Clear();
        PdoVariables.Clear();
        StatusText = "未连接";
        WkcText = "-";
        OnPropertyChanged(nameof(HasSlaves));
        UpdateDeviceModelFlags();
        Log("已断开");
    }

    private void Scan()
    {
        if (_master == null) return;
        try
        {
            Slaves.Clear();
            PdoVariables.Clear();
            Drives.Clear();
            IoModules.Clear();
            IsOperational = false;
            IsCyclicRunning = false;

            int count = _master.Scan();
            foreach (var slave in _master.Slaves)
                Slaves.Add(new SlaveViewModel(slave));

            SelectedSlave = Slaves.FirstOrDefault();
            OnPropertyChanged(nameof(HasSlaves));
            StatusText = $"扫描完成：{count} 个从站";

            if (count <= 0)
            {
                UpdateDeviceModelFlags();
                return;
            }

            // 扫描后即可构建设备列表（伺服/IO 立即可见），无需等待配置
            BuildDeviceModels();

            // 仿真模式下一步到位：自动配置进 OP 并启动周期，三个页签立即有数据
            if (UseSimulation)
                Configure();
        }
        catch (Exception ex)
        {
            Log($"扫描失败：{ex.Message}");
        }
    }

    private void Configure()
    {
        if (_master == null) return;
        try
        {
            bool ok = _master.Configure(UseDistributedClock);
            IsOperational = ok;
            StatusText = ok ? "已进入 OP" : "配置失败，未进入 OP";
            if (!ok) return;

            BuildDeviceModels();
            foreach (var slave in Slaves)
                slave.Refresh();

            // 配置成功后自动启动周期任务，伺服/IO 操作立即生效
            StartCyclic();
        }
        catch (Exception ex)
        {
            Log($"配置失败：{ex.Message}");
        }
    }

    private void BuildDeviceModels()
    {
        if (_master == null) return;

        Drives.Clear();
        AcsDrives.Clear();
        IoModules.Clear();
        PdoVariables.Clear();

        foreach (var drive in DeviceFactory.FindDrives(_master))
            Drives.Add(new DriveViewModel(drive));
        foreach (var acs in DeviceFactory.FindAcsDrives(_master, Log))
            AcsDrives.Add(new AcsDriveViewModel(acs));
        foreach (var module in DeviceFactory.FindIoModules(_master))
            IoModules.Add(new IoModuleViewModel(module));

        foreach (var slave in _master.Slaves)
        {
            foreach (var output in slave.Outputs)
                PdoVariables.Add(new PdoVariableViewModel(output, slave.SlaveIndex));
            foreach (var input in slave.Inputs)
                PdoVariables.Add(new PdoVariableViewModel(input, slave.SlaveIndex));
        }

        Log($"识别到 {Drives.Count} 个伺服、{IoModules.Count} 个 IO 模块、{PdoVariables.Count} 个 PDO 变量");
        UpdateDeviceModelFlags();
    }

    private void UpdateDeviceModelFlags()
    {
        OnPropertyChanged(nameof(HasDrives));
        OnPropertyChanged(nameof(HasAcsDrives));
        OnPropertyChanged(nameof(HasIoModules));
        OnPropertyChanged(nameof(HasPdoData));
    }

    private void StartCyclic()
    {
        if (_master == null) return;
        _master.CycleTimeUs = CycleTimeUs;
        _master.StartCyclic();
        IsCyclicRunning = true;
        StatusText = $"周期运行中（{CycleTimeUs} µs）";
        Log("启动周期通信");
    }

    private void StopCyclic()
    {
        _master?.StopCyclic();
        IsCyclicRunning = false;
        StatusText = "周期已停止";
        Log("停止周期通信");
    }

    private void LoadEsi()
    {
        _esi.Clear();
        int loaded = _esi.LoadDirectory(EsiDirectory, true, new DelegateLog(Log));
        OnPropertyChanged(nameof(EsiSummary));
        Log($"ESI 加载完成：{loaded} 个设备（目录 {EsiDirectory}）");
    }

    public static string EsiDirectory
    {
        get
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
    }

    private void Refresh()
    {
        if (_master == null) return;

        try
        {
            WkcText = $"{_master.LastWorkingCounter} / {_master.ExpectedWorkingCounter}（错误 {_master.WorkingCounterErrors}）";

            _master.RefreshSlaveStates();
            foreach (var slave in Slaves)
                slave.Refresh();
            foreach (var drive in Drives)
                drive.Refresh();
            foreach (var acs in AcsDrives)
                acs.Refresh();
            foreach (var module in IoModules)
                module.Refresh();
            foreach (var variable in PdoVariables)
                variable.Refresh();
        }
        catch (Exception ex)
        {
            Log($"刷新异常：{ex.Message}");
        }
    }

    private void Log(string message)
    {
        void Add()
        {
            LogLines.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
            while (LogLines.Count > 400)
                LogLines.RemoveAt(0);
        }

        if (Dispatcher.UIThread.CheckAccess())
            Add();
        else
            Dispatcher.UIThread.Post(Add);
    }

    private void RaiseCommandStates()
    {
        foreach (var command in new ICommand[]
                 {
                     ConnectCommand, DisconnectCommand, ScanCommand, ConfigureCommand,
                     StartCyclicCommand, StopCyclicCommand
                 })
        {
            (command as RelayCommand)?.RaiseCanExecuteChanged();
        }
        OnPropertyChanged(nameof(HasSlaves));
    }

    public void Dispose()
    {
        _refreshTimer.Stop();
        try
        {
            _master?.StopCyclic();
            _master?.Dispose();
            _link?.Dispose();
        }
        catch { /* 忽略 */ }
    }
}

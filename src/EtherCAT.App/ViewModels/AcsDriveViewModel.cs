using System;
using System.Windows.Input;
using EtherCAT.Devices;

namespace EtherCAT.App.ViewModels;

/// <summary>
/// ACS 驱动器在 UI 中的视图模型：把 AcsDrive 的高层操作暴露成命令与可绑定属性，
/// 并实时刷新状态。逻辑与“伺服驱动”页类似，但面向 ACS 的典型操作流程
/// （使能并回零、点动、绝对/相对定位、速度/转矩模式）。
/// </summary>
public sealed class AcsDriveViewModel : ViewModelBase
{
    private readonly AcsDrive _drive;
    private int _targetVelocity = 100000;
    private int _targetPosition;
    private short _targetTorque;
    private CiA402Mode _selectedMode = CiA402Mode.ProfilePosition;

    public AcsDriveViewModel(AcsDrive drive)
    {
        _drive = drive;

        EnableCommand = new RelayCommand(() => Run(() => _drive.Enable()));
        DisableCommand = new RelayCommand(() => Run(_drive.Disable));
        FaultResetCommand = new RelayCommand(() => Run(_drive.FaultReset));
        StopCommand = new RelayCommand(() => Run(_drive.Stop));
        QuickStopCommand = new RelayCommand(() => Run(_drive.QuickStop));
        EnableAndHomeCommand = new RelayCommand(() => Run(() => _drive.EnableAndHome(17)));
        HomeCommand = new RelayCommand(() => Run(() => _drive.Home(17)));
        JogForwardCommand = new RelayCommand(() => Run(() => _drive.Jog(Math.Abs(_targetVelocity))));
        JogBackwardCommand = new RelayCommand(() => Run(() => _drive.Jog(-Math.Abs(_targetVelocity))));
        MoveAbsoluteCommand = new RelayCommand(() => Run(() => _drive.MoveAbsolute(_targetPosition)));
        MoveRelativeCommand = new RelayCommand(() => Run(() => _drive.MoveRelative(_targetPosition)));
        VelocityCommand = new RelayCommand(() => Run(() => _drive.SetVelocity(_targetVelocity)));
        TorqueCommand = new RelayCommand(() => Run(() => _drive.SetTorque(_targetTorque)));
    }

    private void Run(Action action)
    {
        try
        {
            LastError = "";
            action();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
        OnPropertyChanged(nameof(LastError));
    }

    public AcsDrive Drive => _drive;
    public string DisplayName => _drive.DisplayName;
    public string LastError { get; private set; } = "";

    public ICommand EnableCommand { get; }
    public ICommand DisableCommand { get; }
    public ICommand FaultResetCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand QuickStopCommand { get; }
    public ICommand EnableAndHomeCommand { get; }
    public ICommand HomeCommand { get; }
    public ICommand JogForwardCommand { get; }
    public ICommand JogBackwardCommand { get; }
    public ICommand MoveAbsoluteCommand { get; }
    public ICommand MoveRelativeCommand { get; }
    public ICommand VelocityCommand { get; }
    public ICommand TorqueCommand { get; }

    public CiA402Mode[] Modes { get; } =
    {
        CiA402Mode.ProfilePosition,
        CiA402Mode.ProfileVelocity,
        CiA402Mode.Homing,
        CiA402Mode.CyclicSynchronousTorque,
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
                OnPropertyChanged(nameof(LastError));
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

    public short TargetTorque
    {
        get => _targetTorque;
        set => SetProperty(ref _targetTorque, value);
    }

    public string StateText => _drive.StateText;
    public string StatuswordText => $"0x{_drive.Statusword:X4}";
    public string PositionText => _drive.Position.ToString("N0");
    public string VelocityText => _drive.Velocity.ToString("N0");
    public string TorqueText => _drive.Torque.ToString();
    public string ErrorText => $"0x{_drive.ErrorCode:X4}";
    public string ModeText => _drive.Mode.ToString();
    public bool IsEnabled => _drive.Enabled;
    public bool IsFault => _drive.Fault;
    public bool HomingAttained => _drive.HomingAttained;
    public bool TargetReached => _drive.TargetReached;

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
        OnPropertyChanged(nameof(IsFault));
        OnPropertyChanged(nameof(HomingAttained));
        OnPropertyChanged(nameof(TargetReached));
    }
}

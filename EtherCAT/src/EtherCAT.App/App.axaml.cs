using System.Text;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using EtherCAT.App.ViewModels;
using EtherCAT.App.Views;

namespace EtherCAT.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = new MainViewModel();
            desktop.MainWindow = new MainWindow { DataContext = viewModel };
            desktop.ShutdownRequested += (_, _) => viewModel.Dispose();

            if (desktop.Args is { } args && args.Contains("--selftest"))
            {
                _ = RunSelfTestAsync(desktop, viewModel);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// 界面逻辑自检：连接（仿真）→ 扫描 → 配置 OP → 使能伺服 → IO 置位，结果写入 selftest.log
    /// 用法：dotnet run --project src/EtherCAT.App -- --selftest
    /// </summary>
    private static async Task RunSelfTestAsync(IClassicDesktopStyleApplicationLifetime desktop, MainViewModel vm)
    {
        var report = new StringBuilder();
        try
        {
            await Task.Delay(500);

            await Dispatcher.UIThread.InvokeAsync(() => vm.ConnectCommand.Execute(null));
            await Dispatcher.UIThread.InvokeAsync(() => vm.ScanCommand.Execute(null));
            await Dispatcher.UIThread.InvokeAsync(() => vm.ConfigureCommand.Execute(null));
            await Task.Delay(300);

            report.AppendLine($"从站={vm.Slaves.Count} 伺服={vm.Drives.Count} IO={vm.IoModules.Count} PDO变量={vm.PdoVariables.Count}");
            report.AppendLine($"WKC={vm.WkcText}  状态={vm.StatusText}");

            if (vm.Drives.FirstOrDefault() is { } drive)
            {
                await Dispatcher.UIThread.InvokeAsync(() => drive.EnableCommand.Execute(null));
                await Task.Delay(200);
                report.AppendLine($"伺服：{drive.DisplayName} 状态={drive.StateText} 状态字={drive.StatuswordText} " +
                                  $"位置={drive.PositionText} 速度={drive.VelocityText} 模式={drive.ModeText}");
            }

            if (vm.IoModules.FirstOrDefault() is { } io)
            {
                await Dispatcher.UIThread.InvokeAsync(() => io.AllOutputsOnCommand.Execute(null));
                await Task.Delay(200);
                report.AppendLine($"IO：{io.DisplayName} 输出={io.OutputWordText} 输入={io.InputWordText}");
            }

            report.AppendLine("日志尾部：" + string.Join(" | ", vm.LogLines.TakeLast(6)));
        }
        catch (Exception ex)
        {
            report.AppendLine("异常：" + ex);
        }

        var path = Path.Combine(AppContext.BaseDirectory, "selftest.log");
        await File.WriteAllTextAsync(path, report.ToString());
        desktop.Shutdown();
    }
}

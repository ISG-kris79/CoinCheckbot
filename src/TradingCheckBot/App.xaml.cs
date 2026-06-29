using System.Windows;
using TradingCheckBot.Services;

namespace TradingCheckBot;

public partial class App : Application
{
    public App()
    {
        // Velopack 훅은 WPF 초기화 이전에 가장 먼저 실행되어야 한다.
        UpdateService.HandleStartupHooks();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "오류", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        // 시작 후 백그라운드로 업데이트 확인 (설치본일 때만 동작)
        _ = UpdateService.CheckAndApplyAsync();
    }
}

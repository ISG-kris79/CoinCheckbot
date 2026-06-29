using System.Windows;
using Velopack;
using Velopack.Sources;

namespace TradingCheckBot.Services;

/// <summary>
/// Velopack 기반 자동 업데이트.
/// GitHub Releases(ISG-kris79/CoinCheckbot, public)에서 새 버전을 확인·다운로드해 자동 적용한다.
/// 공개 저장소라 토큰 없이 동작한다. (저장소를 다시 비공개로 바꿀 경우
/// 환경변수 COINCHECKBOT_GH_TOKEN 에 GitHub 토큰을 넣으면 그대로 사용된다.)
/// </summary>
public static class UpdateService
{
    private const string RepoUrl = "https://github.com/ISG-kris79/CoinCheckbot";

    /// <summary>
    /// 앱 시작 시 가장 먼저 호출. 설치/업데이트/제거 훅을 처리한다.
    /// (설치 직후·업데이트 적용 시 특수 인자로 실행되며, 이 호출이 해당 동작을 수행하고 프로세스를 종료할 수 있다.)
    /// </summary>
    public static void HandleStartupHooks()
    {
        VelopackApp.Build().Run();
    }

    /// <summary>
    /// 백그라운드에서 업데이트를 확인하고, 있으면 받아서 적용 후 재시작한다.
    /// 설치본이 아니거나(개발 실행) 토큰이 없으면 조용히 건너뛴다.
    /// </summary>
    public static async Task CheckAndApplyAsync(bool prompt = true)
    {
        try
        {
            string? token = Environment.GetEnvironmentVariable("COINCHECKBOT_GH_TOKEN");
            var source = new GithubSource(RepoUrl, token, prerelease: false);
            var mgr = new UpdateManager(source);

            // 설치형(Velopack 설치본)이 아니면 업데이트 대상이 아님 (개발 중 dotnet run 등)
            if (!mgr.IsInstalled) return;

            var newVersion = await mgr.CheckForUpdatesAsync();
            if (newVersion == null) return; // 최신 버전

            await mgr.DownloadUpdatesAsync(newVersion);

            if (prompt)
            {
                var result = MessageBox.Show(
                    $"새 버전 {newVersion.TargetFullRelease.Version} 이(가) 있습니다.\n지금 업데이트하고 재시작할까요?",
                    "업데이트 알림", MessageBoxButton.YesNo, MessageBoxImage.Information);
                if (result != MessageBoxResult.Yes) return;
            }

            mgr.ApplyUpdatesAndRestart(newVersion);
        }
        catch
        {
            // 네트워크/토큰/지역 차단 등 업데이트 실패는 앱 동작에 영향 주지 않도록 무시
        }
    }
}

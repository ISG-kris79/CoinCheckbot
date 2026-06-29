using System.Collections.Concurrent;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TradingCheckBot.Indicators;
using TradingCheckBot.Services;

namespace TradingCheckBot;

public partial class ScannerWindow : Window
{
    private readonly BinanceClient _client = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(30) };

    private static readonly (string Label, string Interval)[] Timeframes =
    {
        ("1분", "1m"), ("3분", "3m"), ("5분", "5m"), ("15분", "15m"),
    };

    private string _interval = "5m";
    private bool _busy;

    // 코인 선택 상태
    private readonly List<SymbolPick> _allPicks = new();
    private readonly Dictionary<string, BinanceClient.SymbolTicker> _tickerMap = new();

    public ScannerWindow()
    {
        InitializeComponent();
        BuildTimeframeButtons();
        _timer.Tick += async (_, _) => await ScanAsync();
        Loaded += async (_, _) => await LoadSymbolsAsync();
    }

    private void BuildTimeframeButtons()
    {
        foreach (var (label, interval) in Timeframes)
        {
            var rb = new RadioButton
            {
                Content = label,
                Style = (Style)FindResource("TfButton"),
                IsChecked = interval == _interval,
                GroupName = "tf",
                Tag = interval
            };
            rb.Checked += (s, _) => _interval = (string)((RadioButton)s!).Tag;
            TfPanel.Children.Add(rb);
        }
    }

    // 커스텀 타이틀바 창 제어
    private void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Max_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        MaxBtn.Content = ((char)(WindowState == WindowState.Maximized ? 0xE923 : 0xE922)).ToString();
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ───────────────────────── 코인 목록 로드 / 선택 ─────────────────────────

    private async Task LoadSymbolsAsync()
    {
        try
        {
            StatusText.Text = "코인 목록 불러오는 중...";
            var tickers = await _client.GetTopSymbolsAsync(500); // 거래대금 순 전체

            var bull = (Brush)FindResource("BullBrush");
            var bear = (Brush)FindResource("BearBrush");

            _allPicks.Clear();
            _tickerMap.Clear();
            for (int i = 0; i < tickers.Count; i++)
            {
                var t = tickers[i];
                _tickerMap[t.Symbol] = t;
                var pick = new SymbolPick
                {
                    Symbol = t.Symbol,
                    ChangeText = $"{(t.PriceChangePercent >= 0 ? "+" : "")}{t.PriceChangePercent:F1}%",
                    ChangeBrush = t.PriceChangePercent >= 0 ? bull : bear,
                    IsSelected = i < 20 // 기본: 거래대금 상위 20개 선택
                };
                pick.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SymbolPick.IsSelected)) UpdateSelCount(); };
                _allPicks.Add(pick);
            }

            ApplyFilter();
            UpdateSelCount();
            PickHint.Visibility = Visibility.Collapsed;
            StatusText.Text = $"코인 {_allPicks.Count}개 로드 완료 · 스캔할 코인을 선택하세요";
        }
        catch (Exception ex)
        {
            PickHint.Text = "코인 목록 로드 실패\n(지역 차단/네트워크 확인)";
            StatusText.Text = "오류: " + ex.Message;
        }
    }

    private void ApplyFilter()
    {
        string q = FilterBox.Text.Trim().ToUpperInvariant();
        IEnumerable<SymbolPick> view = _allPicks;
        if (q.Length > 0) view = _allPicks.Where(p => p.Symbol.Contains(q, StringComparison.Ordinal));
        PickList.ItemsSource = view.ToList();
    }

    private void UpdateSelCount() => SelCountText.Text = $"{_allPicks.Count(p => p.IsSelected)}개 선택";

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void SelectTop20_Click(object sender, RoutedEventArgs e)
    {
        for (int i = 0; i < _allPicks.Count; i++) _allPicks[i].IsSelected = i < 20;
        UpdateSelCount();
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        // 현재 필터에 보이는 항목만 전체 선택
        foreach (var p in (IEnumerable<SymbolPick>)(PickList.ItemsSource ?? _allPicks)) p.IsSelected = true;
        UpdateSelCount();
    }

    private void ClearSel_Click(object sender, RoutedEventArgs e)
    {
        foreach (var p in _allPicks) p.IsSelected = false;
        UpdateSelCount();
    }

    // ───────────────────────── 스캔 ─────────────────────────

    private async void Scan_Click(object sender, RoutedEventArgs e) => await ScanAsync();

    private void AutoRefresh_Changed(object sender, RoutedEventArgs e)
    {
        if (AutoRefresh.IsChecked == true) _timer.Start();
        else _timer.Stop();
    }

    private int SelectedMinScore() => (MinScoreBox.SelectedIndex) switch { 1 => 45, 2 => 60, 3 => 75, _ => 0 };

    private async Task ScanAsync()
    {
        if (_busy) return;

        var symbols = _allPicks.Where(p => p.IsSelected).Select(p => p.Symbol).ToList();
        if (symbols.Count == 0)
        {
            EmptyHint.Visibility = Visibility.Visible;
            EmptyHint.Text = "스캔할 코인을 먼저 선택하세요 (왼쪽 목록에서 체크)";
            StatusText.Text = "선택된 코인이 없습니다";
            return;
        }

        _busy = true;
        ScanBtn.IsEnabled = false;
        int minScore = SelectedMinScore();

        try
        {
            StatusText.Text = $"선택한 {symbols.Count}개 코인 {_interval} 스캔 중...";

            var rows = new ConcurrentBag<ScalpRow>();
            int done = 0;
            using var sem = new SemaphoreSlim(8); // 동시 요청 8개 제한

            var tasks = symbols.Select(async sym =>
            {
                await sem.WaitAsync();
                try
                {
                    var candles = await _client.GetKlinesAsync(sym, _interval, 160);
                    if (candles.Count >= 60)
                    {
                        var r = ScalpEngine.Evaluate(sym, _interval, candles);
                        rows.Add(BuildRow(r, _tickerMap.GetValueOrDefault(sym)));
                    }
                }
                catch { /* 개별 종목 실패는 건너뜀 */ }
                finally
                {
                    int d = Interlocked.Increment(ref done);
                    _ = Dispatcher.BeginInvoke(() => StatusText.Text = $"{_interval} 스캔 중... {d}/{symbols.Count}");
                    sem.Release();
                }
            });
            await Task.WhenAll(tasks);

            var ranked = rows
                .Where(r => r.Score >= minScore)
                .OrderByDescending(r => r.Score)
                .ToList();

            for (int i = 0; i < ranked.Count; i++) ranked[i].Rank = i + 1;

            ResultList.ItemsSource = ranked;
            EmptyHint.Visibility = ranked.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyHint.Text = rows.Count == 0
                ? "데이터를 불러오지 못했습니다 (지역 차단/네트워크 확인)"
                : $"최소 점수 {minScore}점 이상 종목이 없습니다";

            int strong = ranked.Count(r => r.Score >= 60);
            StatusText.Text = $"완료 · {_interval} · 선택 {symbols.Count}개 스캔 · 조건충족 {ranked.Count}개(매수우위 {strong}개) · " +
                              $"갱신 {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            StatusText.Text = "오류: " + ex.Message;
            EmptyHint.Visibility = Visibility.Visible;
            EmptyHint.Text = "스캔 실패: " + ex.Message;
        }
        finally
        {
            ScanBtn.IsEnabled = true;
            _busy = false;
        }
    }

    private ScalpRow BuildRow(ScalpResult r, BinanceClient.SymbolTicker? t)
    {
        var bull = (Brush)FindResource("BullBrush");
        var bear = (Brush)FindResource("BearBrush");
        var muted = (Brush)FindResource("MutedBrush");
        var accent = (Brush)FindResource("AccentBrush");

        Brush grade = r.Score switch
        {
            >= 75 => bull,
            >= 60 => new SolidColorBrush(Color.FromRgb(0x6B, 0xCB, 0x77)),
            >= 45 => accent,
            _ => muted
        };

        double chg = t?.PriceChangePercent ?? 0;
        return new ScalpRow
        {
            Symbol = r.Symbol,
            Score = r.Score,
            Grade = r.Grade,
            GradeBrush = grade,
            BarWidth = 146.0 * r.Score / 100.0,
            ChangeText = $"24h {(chg >= 0 ? "+" : "")}{chg:F2}%",
            ChangeBrush = chg >= 0 ? bull : bear,
            RrText = $"손익비 1:{r.RiskReward:F1}",
            LevelsText = $"진입 {Fmt(r.Entry)}  ·  목표 {Fmt(r.Target)}  ·  손절 {Fmt(r.Stop)}",
            Reasons = r.Reasons
        };
    }

    private static string Fmt(double v)
    {
        if (v >= 1000) return v.ToString("N1");
        if (v >= 1) return v.ToString("N3");
        return v.ToString("0.######");
    }
}

/// <summary>코인 선택 목록의 한 항목</summary>
public sealed class SymbolPick : INotifyPropertyChanged
{
    public required string Symbol { get; init; }
    public required string ChangeText { get; init; }
    public required Brush ChangeBrush { get; init; }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>스캐너 결과 1행 (UI 바인딩용)</summary>
public sealed class ScalpRow
{
    public int Rank { get; set; }
    public required string Symbol { get; init; }
    public required int Score { get; init; }
    public required string Grade { get; init; }
    public required Brush GradeBrush { get; init; }
    public required double BarWidth { get; init; }
    public required string ChangeText { get; init; }
    public required Brush ChangeBrush { get; init; }
    public required string RrText { get; init; }
    public required string LevelsText { get; init; }
    public required List<string> Reasons { get; init; }
}

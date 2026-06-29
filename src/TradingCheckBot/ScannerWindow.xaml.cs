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
    private bool _lastWasSearch; // 자동갱신이 마지막 실행 모드(스캔/서칭)를 유지

    // 코인 선택 상태
    private readonly List<SymbolPick> _allPicks = new();
    private readonly Dictionary<string, BinanceClient.SymbolTicker> _tickerMap = new();

    public ScannerWindow()
    {
        InitializeComponent();
        BuildTimeframeButtons();
        _timer.Tick += async (_, _) => { if (_lastWasSearch) await SearchPredictAsync(); else await ScanAsync(); };
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

    // ───────────────────────── 예상상승 서칭 (과거통계 + 셋업) ─────────────────────────
    private async void Search_Click(object sender, RoutedEventArgs e) => await SearchPredictAsync();

    private async Task SearchPredictAsync()
    {
        if (_busy) return;
        if (_allPicks.Count == 0)
        {
            EmptyHint.Visibility = Visibility.Visible;
            EmptyHint.Text = "코인 목록을 먼저 불러온 뒤 다시 시도하세요";
            return;
        }
        _busy = true;
        _lastWasSearch = true;
        SearchBtn.IsEnabled = false;
        ScanBtn.IsEnabled = false;

        try
        {
            // 선택한 코인이 있으면 그것만, 없으면 거래대금 상위 50개를 서칭
            var selected = _allPicks.Where(p => p.IsSelected).Select(p => p.Symbol).ToList();
            var syms = selected.Count > 0 ? selected : _allPicks.Take(50).Select(p => p.Symbol).ToList();
            string scope = selected.Count > 0 ? $"선택 {syms.Count}개" : $"상위 {syms.Count}개";
            StatusText.Text = $"예상상승 서칭 중... {scope} 과거데이터 분석";

            var rows = new ConcurrentBag<ScalpRow>();
            int done = 0;
            using var sem = new SemaphoreSlim(6);
            var tasks = syms.Select(async sym =>
            {
                await sem.WaitAsync();
                try
                {
                    var candles = await _client.GetKlinesAsync(sym, _interval, 600);
                    if (candles.Count >= 120)
                    {
                        var pr = PredictEngine.Predict(sym, _interval, candles);
                        rows.Add(BuildPredictRow(pr, _tickerMap.GetValueOrDefault(sym)));
                    }
                }
                catch { /* 개별 실패 무시 */ }
                finally
                {
                    int d = Interlocked.Increment(ref done);
                    _ = Dispatcher.BeginInvoke(() => StatusText.Text = $"예상상승 서칭 중... {d}/{syms.Count}");
                    sem.Release();
                }
            });
            await Task.WhenAll(tasks);

            var ranked = rows.OrderByDescending(r => r.Score).ToList();
            for (int i = 0; i < ranked.Count; i++) ranked[i].Rank = i + 1;

            ResultList.ItemsSource = ranked;
            EmptyHint.Visibility = ranked.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyHint.Text = "데이터를 불러오지 못했습니다 (지역 차단/네트워크 확인)";

            int promising = ranked.Count(r => r.Score >= 65);
            StatusText.Text = $"서칭 완료 · {_interval} · {ranked.Count}개 · 유망 {promising}개 · 확률가중 예상상승 순 · 갱신 {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            StatusText.Text = "오류: " + ex.Message;
            EmptyHint.Visibility = Visibility.Visible;
            EmptyHint.Text = "서칭 실패: " + ex.Message;
        }
        finally
        {
            SearchBtn.IsEnabled = true;
            ScanBtn.IsEnabled = true;
            _busy = false;
        }
    }

    private ScalpRow BuildPredictRow(PredictResult r, BinanceClient.SymbolTicker? t)
    {
        var bull = (Brush)FindResource("BullBrush");
        var bear = (Brush)FindResource("BearBrush");
        var muted = (Brush)FindResource("MutedBrush");
        var accent = (Brush)FindResource("AccentBrush");

        bool isLong = r.Side == TradeSide.Long;
        // 롱=초록 계열, 숏=빨강 계열 (점수 낮으면 흐리게)
        Brush tier = r.Score >= 45 ? (isLong ? bull : bear) : muted;
        // 방향 부호: 롱은 +상승률, 숏은 -하락률
        string moveTxt = isLong ? $"+{r.ExpectedMovePct:F1}%" : $"-{r.ExpectedMovePct:F1}%";

        double chg = t?.PriceChangePercent ?? 0;
        string winTxt = r.Samples > 0
            ? $"{r.SideText} · 승률 {r.WinRate:F0}% · 표본 {r.Samples} · 손익비 1:{r.RiskReward:F1}"
            : $"{r.SideText} · 과거표본 부족(셋업 기준)";
        return new ScalpRow
        {
            Symbol = r.Symbol,
            Order = isLong ? 0 : 1, // 동점 시 롱 먼저
            Score = (int)Math.Round(r.Score),
            Grade = $"{r.SideText} {r.Tier} {moveTxt}",
            GradeBrush = tier,
            BarWidth = 146.0 * Math.Clamp(r.Score, 0, 100) / 100.0,
            ChangeText = $"24h {(chg >= 0 ? "+" : "")}{chg:F2}%",
            ChangeBrush = chg >= 0 ? bull : bear,
            RrText = winTxt,
            LevelsText = $"{r.Setup}\n진입 {Fmt(r.Entry)}  ·  목표 {Fmt(r.Target)}  ·  손절 {Fmt(r.Stop)}",
            Reasons = r.Reasons
        };
    }

    private void AutoRefresh_Changed(object sender, RoutedEventArgs e)
    {
        if (AutoRefresh.IsChecked == true) _timer.Start();
        else _timer.Stop();
    }

    // 표시 필터: 0=진입만, 1=진입+대기, 2=전체
    private int FilterMode() => MinScoreBox.SelectedIndex switch { 0 => 0, 2 => 2, _ => 1 };

    private static int DecisionOrder(ScalpDecision d) => d switch
    {
        ScalpDecision.Enter => 0,
        ScalpDecision.Wait => 1,
        _ => 2
    };

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
        _lastWasSearch = false;
        ScanBtn.IsEnabled = false;
        int filterMode = FilterMode();

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
                    var candles = await _client.GetKlinesAsync(sym, _interval, 500); // 메인창과 동일 개수 → 판정 일치
                    if (candles.Count >= 60)
                    {
                        var r = PlanManager.Evaluate(sym, _interval, candles);
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

            var all = rows.ToList();
            var ranked = all
                .Where(r => r.Order <= filterMode || filterMode == 2)
                .OrderBy(r => r.Order)
                .ThenByDescending(r => r.Score)
                .ToList();

            for (int i = 0; i < ranked.Count; i++) ranked[i].Rank = i + 1;

            ResultList.ItemsSource = ranked;
            EmptyHint.Visibility = ranked.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyHint.Text = all.Count == 0
                ? "데이터를 불러오지 못했습니다 (지역 차단/네트워크 확인)"
                : "조건에 맞는 종목이 없습니다 (필터를 넓혀보세요)";

            int enters = all.Count(r => r.Order == 0);
            int waits = all.Count(r => r.Order == 1);
            StatusText.Text = $"완료 · {_interval} · 선택 {symbols.Count}개 · 🟢진입 {enters} · 🟡대기 {waits} · 표시 {ranked.Count} · " +
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

        Brush decBrush = r.Decision == ScalpDecision.Enter
            ? (r.Side == TradeSide.Long ? bull : bear)
            : r.Decision == ScalpDecision.Wait ? accent : muted;

        // 진입일 때만 손익비/레벨이 의미 있음
        string levels = r.Decision == ScalpDecision.Enter
            ? $"{r.Trigger}\n진입 {Fmt(r.Entry)}  ·  목표 {Fmt(r.Target)}  ·  손절 {Fmt(r.Stop)}"
            : r.Trigger;

        double chg = t?.PriceChangePercent ?? 0;
        return new ScalpRow
        {
            Symbol = r.Symbol,
            Order = DecisionOrder(r.Decision),
            Score = r.Quality,
            Grade = r.DecisionText,
            GradeBrush = decBrush,
            BarWidth = 146.0 * r.Quality / 100.0,
            ChangeText = $"24h {(chg >= 0 ? "+" : "")}{chg:F2}%",
            ChangeBrush = chg >= 0 ? bull : bear,
            RrText = r.Decision == ScalpDecision.Enter ? $"손익비 1:{r.RiskReward:F1}" : "",
            LevelsText = levels,
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
    /// <summary>판정 정렬 순서 (0=진입,1=대기,2=회피)</summary>
    public required int Order { get; init; }
    /// <summary>진입 품질 0~100 (게이지/정렬용)</summary>
    public required int Score { get; init; }
    /// <summary>판정 텍스트 (진입/대기/회피)</summary>
    public required string Grade { get; init; }
    public required Brush GradeBrush { get; init; }
    public required double BarWidth { get; init; }
    public required string ChangeText { get; init; }
    public required Brush ChangeBrush { get; init; }
    public required string RrText { get; init; }
    public required string LevelsText { get; init; }
    public required List<string> Reasons { get; init; }
}

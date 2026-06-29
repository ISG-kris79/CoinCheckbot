using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using TradingCheckBot.Indicators;
using TradingCheckBot.Models;
using TradingCheckBot.Services;

namespace TradingCheckBot;

public partial class MainWindow : Window
{
    private readonly BinanceClient _client = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(15) };

    // (표시 라벨, 바이낸스 인터벌)
    private static readonly (string Label, string Interval)[] Timeframes =
    {
        ("1분", "1m"), ("5분", "5m"), ("15분", "15m"), ("1시간", "1h"),
        ("4시간", "4h"), ("일봉", "1d"), ("주봉", "1w"), ("월봉", "1M"),
    };

    private string _currentInterval = "15m";
    private List<Candle> _candles = new();
    private bool _busy;

    private readonly List<CoinItem> _allCoins = new();
    private bool _suppressCoinSelect;
    private ScalpResult? _lastScalp; // 차트에 진입/익절/손절 표시용

    public MainWindow()
    {
        InitializeComponent();
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        if (v != null) VersionText.Text = $"v{v.Major}.{v.Minor}.{v.Build}";
        BuildTimeframeButtons();
        _timer.Tick += async (_, _) => await LoadAsync();
        Loaded += async (_, _) =>
        {
            await LoadCoinListAsync();
            await LoadAsync();
        };
    }

    // ───────────────────────── 코인 목록 ─────────────────────────
    private async Task LoadCoinListAsync()
    {
        try
        {
            var bull = (Brush)FindResource("BullBrush");
            var bear = (Brush)FindResource("BearBrush");
            var tickers = await _client.GetTopSymbolsAsync(500);
            _allCoins.Clear();
            foreach (var t in tickers)
                _allCoins.Add(new CoinItem
                {
                    Symbol = t.Symbol,
                    ChangeText = $"{(t.PriceChangePercent >= 0 ? "+" : "")}{t.PriceChangePercent:F1}%",
                    ChangeBrush = t.PriceChangePercent >= 0 ? bull : bear
                });
            ApplyCoinFilter();
            CoinListHint.Visibility = _allCoins.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            CoinListHint.Text = "목록 없음";
        }
        catch
        {
            CoinListHint.Visibility = Visibility.Visible;
            CoinListHint.Text = "목록 로드 실패";
        }
    }

    private void ApplyCoinFilter()
    {
        string q = CoinFilterBox.Text.Trim().ToUpperInvariant();
        IEnumerable<CoinItem> view = _allCoins;
        if (q.Length > 0) view = _allCoins.Where(c => c.Symbol.Contains(q, StringComparison.Ordinal));
        var list = view.ToList();
        string current = SymbolBox.Text.Trim().ToUpperInvariant();

        _suppressCoinSelect = true;
        CoinList.ItemsSource = list;
        CoinList.SelectedItem = list.FirstOrDefault(c => c.Symbol == current);
        _suppressCoinSelect = false;
    }

    private void CoinFilterBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyCoinFilter();

    private async void CoinList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressCoinSelect) return;
        if (CoinList.SelectedItem is CoinItem c)
        {
            SymbolBox.Text = c.Symbol;
            await LoadAsync();
        }
    }

    private void BuildTimeframeButtons()
    {
        foreach (var (label, interval) in Timeframes)
        {
            var rb = new RadioButton
            {
                Content = label,
                Style = (Style)FindResource("TfButton"),
                IsChecked = interval == _currentInterval,
                GroupName = "tf",
                Tag = interval
            };
            rb.Checked += async (s, _) =>
            {
                _currentInterval = (string)((RadioButton)s!).Tag;
                await LoadAsync();
            };
            TfPanel.Children.Add(rb);
        }
    }

    // ───────────────────────── 커스텀 타이틀바 창 제어 ─────────────────────────
    private void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Max_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        MaxBtn.Content = ((char)(WindowState == WindowState.Maximized ? 0xE923 : 0xE922)).ToString(); // 복원/최대화 글리프
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private ScannerWindow? _scanner;
    private void Scanner_Click(object sender, RoutedEventArgs e)
    {
        if (_scanner is { IsLoaded: true })
        {
            _scanner.Activate();
            return;
        }
        _scanner = new ScannerWindow { Owner = this };
        _scanner.Closed += (_, _) => _scanner = null;
        _scanner.Show();
    }

    private async void SymbolBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await LoadAsync();
    }

    private void AutoRefresh_Changed(object sender, RoutedEventArgs e)
    {
        if (AutoRefresh.IsChecked == true) _timer.Start();
        else _timer.Stop();
    }

    private async Task LoadAsync()
    {
        if (_busy) return;
        _busy = true;
        var symbol = SymbolBox.Text.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(symbol)) { _busy = false; return; }

        try
        {
            RefreshBtn.IsEnabled = false;
            StatusText.Text = $"{symbol} {_currentInterval} 데이터 불러오는 중...";

            var candles = await _client.GetKlinesAsync(symbol, _currentInterval, 500);
            if (candles.Count < 30)
            {
                StatusText.Text = "캔들 데이터가 너무 적습니다. 심볼을 확인하세요.";
                return;
            }
            _candles = candles;

            var result = StrategyEngine.Evaluate(symbol, _currentInterval, candles);
            RenderResult(result);
            RenderScalp(PlanManager.Evaluate(symbol, _currentInterval, candles));
            DrawChart();

            StatusText.Text = $"{symbol} · {_currentInterval} · 마지막 봉 {result.LastTime:yyyy-MM-dd HH:mm} · " +
                              $"갱신 {DateTime.Now:HH:mm:ss} · 캔들 {candles.Count}개";
        }
        catch (Exception ex)
        {
            StatusText.Text = "오류: " + ex.Message;
            VerdictText.Text = "조회 실패";
            VerdictSub.Text = ex.Message;
        }
        finally
        {
            RefreshBtn.IsEnabled = true;
            _busy = false;
        }
    }

    private void RenderResult(SignalResult r)
    {
        var bull = (Brush)FindResource("BullBrush");
        var bear = (Brush)FindResource("BearBrush");
        var muted = (Brush)FindResource("MutedBrush");

        VerdictText.Text = r.VerdictText;
        VerdictText.Foreground = r.Verdict switch
        {
            Bias.Bull => bull,
            Bias.Bear => bear,
            _ => (Brush)FindResource("TextBrush")
        };
        VerdictBanner.Background = r.Verdict switch
        {
            Bias.Bull => new SolidColorBrush(Color.FromArgb(40, 38, 166, 154)),
            Bias.Bear => new SolidColorBrush(Color.FromArgb(40, 239, 83, 80)),
            _ => (Brush)FindResource("PanelBrush2")
        };
        VerdictSub.Text = $"종합점수 {(r.NetScore > 0 ? "+" : "")}{r.NetScore}  ·  추세 {r.TrendStrength}";

        PriceText.Text = r.LastPrice >= 100 ? r.LastPrice.ToString("N1") : r.LastPrice.ToString("0.######");
        AdxText.Text = r.AdxValue.ToString("F1");
        VoteText.Text = $"▲{r.BullCount} / ▼{r.BearCount}";

        // 게이지
        double pct = r.BullPercent;
        GaugeText.Text = $"상방 {pct}%";
        UpdateGauge(pct);

        // 지표 리스트
        SignalList.ItemsSource = r.Signals.Select(s => new SignalRow(s, bull, bear, muted)).ToList();
    }

    private void RenderScalp(ScalpResult s)
    {
        _lastScalp = s;
        var bull = (Brush)FindResource("BullBrush");
        var accent = (Brush)FindResource("AccentBrush");
        var muted = (Brush)FindResource("MutedBrush");

        var bear = (Brush)FindResource("BearBrush");
        bool isLong = s.Side == TradeSide.Long;
        ScalpVerdict.Text = s.Decision switch
        {
            ScalpDecision.Enter => isLong ? $"롱 진입 ✅ (품질 {s.Quality})" : $"숏 진입 🔻 (품질 {s.Quality})",
            ScalpDecision.Wait => "단타 대기 ⏳",
            _ => "단타 회피 ⛔"
        };
        ScalpVerdict.Foreground = s.Decision switch
        {
            ScalpDecision.Enter => isLong ? bull : bear,
            ScalpDecision.Wait => accent,
            _ => muted
        };
        ScalpBanner.Background = s.Decision switch
        {
            ScalpDecision.Enter => isLong ? new SolidColorBrush(Color.FromArgb(40, 38, 166, 154))
                                          : new SolidColorBrush(Color.FromArgb(40, 239, 83, 80)),
            ScalpDecision.Wait => new SolidColorBrush(Color.FromArgb(36, 240, 185, 11)),
            _ => new SolidColorBrush(Color.FromArgb(34, 0, 0, 0))
        };

        string levels = s.Decision == ScalpDecision.Enter
            ? $"\n진입 {FmtP(s.Entry)} · 목표 {FmtP(s.Target)} · 손절 {FmtP(s.Stop)} (1:{s.RiskReward:F1})"
            : "";
        ScalpNote.Text = $"{s.Trigger}{levels}\n※ {_currentInterval} 확정봉 {s.LastTime:MM/dd HH:mm} 기준 (다음 봉 마감까지 유지)";
    }

    private static string FmtP(double v) => v >= 1000 ? v.ToString("N1") : v >= 1 ? v.ToString("N3") : v.ToString("0.######");

    private void UpdateGauge(double bullPercent)
    {
        // BullBar 폭을 부모 폭 비율로 설정 (레이아웃 이후 적용)
        Dispatcher.BeginInvoke(() =>
        {
            if (BullBar.Parent is FrameworkElement parent && parent.ActualWidth > 0)
                BullBar.Width = parent.ActualWidth * bullPercent / 100.0;
        }, DispatcherPriority.Loaded);
    }

    // ───────────────────────── 캔들 차트 직접 렌더링 ─────────────────────────

    private void ChartCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawChart();

    private void DrawChart()
    {
        ChartCanvas.Children.Clear();
        if (_candles.Count < 2) return;

        double w = ChartCanvas.ActualWidth, h = ChartCanvas.ActualHeight;
        if (w <= 10 || h <= 10) return;

        const int maxBars = 120;
        var view = _candles.Count > maxBars ? _candles.GetRange(_candles.Count - maxBars, maxBars) : _candles;
        int n = view.Count;

        double padRight = 64, padBottom = 18, padTop = 6;
        double plotW = w - padRight, plotH = h - padBottom - padTop;

        double hi = view.Max(c => c.High);
        double lo = view.Min(c => c.Low);
        if (hi <= lo) return;
        double range = hi - lo;
        hi += range * 0.05; lo -= range * 0.05; range = hi - lo;

        double YOf(double price) => padTop + (hi - price) / range * plotH;
        double slot = plotW / n;
        double bodyW = Math.Max(1.5, slot * 0.6);

        var bull = (Brush)FindResource("BullBrush");
        var bear = (Brush)FindResource("BearBrush");
        var border = (Brush)FindResource("BorderBrush");
        var muted = (Brush)FindResource("MutedBrush");

        // 가로 그리드 + 가격 라벨
        for (int g = 0; g <= 4; g++)
        {
            double price = hi - range * g / 4.0;
            double y = YOf(price);
            ChartCanvas.Children.Add(new Line
            {
                X1 = 0, X2 = plotW, Y1 = y, Y2 = y,
                Stroke = border, StrokeThickness = 0.5, StrokeDashArray = new DoubleCollection { 3, 3 }
            });
            var lbl = new TextBlock
            {
                Text = price >= 100 ? price.ToString("N1") : price.ToString("0.####"),
                Foreground = muted, FontSize = 10
            };
            Canvas.SetLeft(lbl, plotW + 4);
            Canvas.SetTop(lbl, y - 8);
            ChartCanvas.Children.Add(lbl);
        }

        // EMA 오버레이
        var closes = _candles.Select(c => c.Close).ToArray();
        int offset = _candles.Count - n;
        DrawEmaLine(Ind.Ema(closes, 20), offset, n, slot, YOf, Color.FromRgb(0xF0, 0xB9, 0x0B));
        DrawEmaLine(Ind.Ema(closes, 50), offset, n, slot, YOf, Color.FromRgb(0x4F, 0x9C, 0xF0));
        DrawEmaLine(Ind.Ema(closes, 200), offset, n, slot, YOf, Color.FromRgb(0xB0, 0x7C, 0xF0));

        // 캔들
        for (int i = 0; i < n; i++)
        {
            var c = view[i];
            double cx = i * slot + slot / 2.0;
            bool up = c.Close >= c.Open;
            var brush = up ? bull : bear;

            ChartCanvas.Children.Add(new Line
            {
                X1 = cx, X2 = cx, Y1 = YOf(c.High), Y2 = YOf(c.Low),
                Stroke = brush, StrokeThickness = 1
            });

            double yOpen = YOf(c.Open), yClose = YOf(c.Close);
            double top = Math.Min(yOpen, yClose);
            double bh = Math.Max(1, Math.Abs(yClose - yOpen));
            var rect = new Rectangle { Width = bodyW, Height = bh, Fill = brush };
            Canvas.SetLeft(rect, cx - bodyW / 2.0);
            Canvas.SetTop(rect, top);
            ChartCanvas.Children.Add(rect);
        }

        // 현재가 라인
        double last = view[^1].Close;
        double ly = YOf(last);
        ChartCanvas.Children.Add(new Line
        {
            X1 = 0, X2 = plotW, Y1 = ly, Y2 = ly,
            Stroke = (Brush)FindResource("AccentBrush"), StrokeThickness = 0.8,
            StrokeDashArray = new DoubleCollection { 2, 2 }
        });
        var priceLbl = new Border
        {
            Background = (Brush)FindResource("AccentBrush"),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 1, 4, 1),
            Child = new TextBlock { Text = last >= 100 ? last.ToString("N1") : last.ToString("0.####"), Foreground = Brushes.Black, FontSize = 10, FontWeight = FontWeights.Bold }
        };
        Canvas.SetLeft(priceLbl, plotW + 2);
        Canvas.SetTop(priceLbl, ly - 9);
        ChartCanvas.Children.Add(priceLbl);

        // 단타 진입/익절/손절 표시 (회피가 아닐 때)
        if (_lastScalp is { } sc && sc.Decision != ScalpDecision.Avoid)
        {
            var accent = (Brush)FindResource("AccentBrush");
            bool isLong = sc.Side == TradeSide.Long;

            void DrawLevel(double p, Brush b, string label)
            {
                double y = YOf(p);
                ChartCanvas.Children.Add(new Line
                {
                    X1 = 0, X2 = plotW, Y1 = y, Y2 = y, Stroke = b, StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 4, 3 }
                });
                var lbl = new Border
                {
                    Background = b, CornerRadius = new CornerRadius(3), Padding = new Thickness(4, 1, 4, 1),
                    Child = new TextBlock { Text = label, Foreground = Brushes.Black, FontSize = 10, FontWeight = FontWeights.Bold }
                };
                Canvas.SetLeft(lbl, plotW + 2);
                Canvas.SetTop(lbl, Math.Clamp(y - 9, padTop, padTop + plotH - 12));
                ChartCanvas.Children.Add(lbl);
            }

            DrawLevel(sc.Target, bull, $"익절 {FmtP(sc.Target)}");
            DrawLevel(sc.Stop, bear, $"손절 {FmtP(sc.Stop)}");
            string entryTag = sc.Decision == ScalpDecision.Enter ? "진입" : "진입대기";
            DrawLevel(sc.Entry, accent, $"{(isLong ? "▲" : "▼")}{entryTag} {FmtP(sc.Entry)}");

            // 진입 화살표 (좌측)
            double ey = YOf(sc.Entry);
            var arrow = new TextBlock
            {
                Text = isLong ? "▲" : "▼", Foreground = isLong ? bull : bear,
                FontSize = 20, FontWeight = FontWeights.Bold
            };
            Canvas.SetLeft(arrow, 6);
            Canvas.SetTop(arrow, Math.Clamp(ey - 13, padTop, padTop + plotH - 20));
            ChartCanvas.Children.Add(arrow);
        }

        ChartTitle.Text = $"{SymbolBox.Text.Trim().ToUpperInvariant()}  ·  {_currentInterval}  · EMA20(노랑)/50(파랑)/200(보라)";
    }

    private void DrawEmaLine(double[] ema, int offset, int n, double slot, Func<double, double> yOf, Color color)
    {
        var poly = new Polyline { Stroke = new SolidColorBrush(color), StrokeThickness = 1.3 };
        for (int i = 0; i < n; i++)
        {
            double v = ema[offset + i];
            if (double.IsNaN(v)) continue;
            poly.Points.Add(new Point(i * slot + slot / 2.0, yOf(v)));
        }
        if (poly.Points.Count > 1) ChartCanvas.Children.Add(poly);
    }
}

/// <summary>코인 목록 1행 (UI 바인딩용)</summary>
public sealed class CoinItem
{
    public required string Symbol { get; init; }
    public required string ChangeText { get; init; }
    public required Brush ChangeBrush { get; init; }
}

/// <summary>지표 신호를 UI 바인딩용으로 감싼 뷰모델</summary>
public sealed class SignalRow
{
    public string Name { get; }
    public string Value { get; }
    public string Comment { get; }
    public string DirText { get; }
    public Brush BarBrush { get; }

    public SignalRow(IndicatorSignal s, Brush bull, Brush bear, Brush muted)
    {
        Name = s.Name;
        Value = s.Value;
        Comment = s.Comment;
        DirText = s.Direction switch { Bias.Bull => "상방", Bias.Bear => "하방", _ => "중립" };
        BarBrush = s.Direction switch { Bias.Bull => bull, Bias.Bear => bear, _ => muted };
    }
}

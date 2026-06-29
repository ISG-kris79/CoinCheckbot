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
            var tickers = await _client.GetTopSymbolsAsync(50); // 시총(거래대금) 상위 50개만
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
        string warn = string.IsNullOrEmpty(s.Warning) ? "" : $"\n{s.Warning}";
        ScalpNote.Text = $"{s.Trigger}{levels}{warn}\n※ {_currentInterval} 확정봉 {s.LastTime:MM/dd HH:mm} 기준 (다음 봉 마감까지 유지)";
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

    // 차트 뷰포트 상태 (팬/줌)
    private double _barsVisible = 120;   // 가로: 보이는 봉 수 (줌)
    private double _offsetFromEnd = 0;   // 가로: 오른쪽 끝에서 떨어진 봉 수 (팬)
    private double _yLo, _yHi;           // 세로: 가격 범위
    private bool _yManual;               // 세로를 사용자가 수동 조정했는가
    private bool _viewInit;
    private string _viewSymbol = "";
    private double _plotW, _plotH, _padTop;
    private bool _dragging;
    private Point _dragStart;
    private double _dOff0, _dBars0, _dYLo0, _dYHi0;

    private void DrawChart()
    {
        ChartCanvas.Children.Clear();
        int count = _candles.Count;
        if (count < 2) return;

        double w = ChartCanvas.ActualWidth, h = ChartCanvas.ActualHeight;
        if (w <= 10 || h <= 10) return;

        string sym = SymbolBox.Text.Trim().ToUpperInvariant();
        if (!_viewInit || _viewSymbol != sym)
        {
            _barsVisible = Math.Min(120, count);
            _offsetFromEnd = 0;
            _yManual = false;
            _viewInit = true;
            _viewSymbol = sym;
        }
        _barsVisible = Math.Clamp(_barsVisible, 10, count);
        _offsetFromEnd = Math.Clamp(_offsetFromEnd, -5, Math.Max(0, count - _barsVisible));

        double padRight = 64, padBottom = 18, padTop = 6;
        double plotW = w - padRight, plotH = h - padBottom - padTop;
        _plotW = plotW; _plotH = plotH; _padTop = padTop;

        double x1 = (count - 1) - _offsetFromEnd; // 오른쪽 끝 인덱스
        double x0 = x1 - _barsVisible + 1;        // 왼쪽 끝 인덱스
        int iLo = Math.Max(0, (int)Math.Floor(x0));
        int iHi = Math.Min(count - 1, (int)Math.Ceiling(x1));

        // 세로 범위: 수동이 아니면 보이는 구간에 자동 맞춤
        if (!_yManual)
        {
            double hh = double.MinValue, ll = double.MaxValue;
            for (int i = iLo; i <= iHi; i++) { hh = Math.Max(hh, _candles[i].High); ll = Math.Min(ll, _candles[i].Low); }
            if (hh <= ll) return;
            double r0 = hh - ll; _yHi = hh + r0 * 0.06; _yLo = ll - r0 * 0.06;
        }
        double range = _yHi - _yLo;
        if (range <= 0) return;

        double XOf(double idx) => (idx - x0 + 0.5) / _barsVisible * plotW;
        double YOf(double price) => padTop + (_yHi - price) / range * plotH;
        double slot = plotW / _barsVisible;
        double bodyW = Math.Max(1.5, slot * 0.6);

        var bull = (Brush)FindResource("BullBrush");
        var bear = (Brush)FindResource("BearBrush");
        var border = (Brush)FindResource("BorderBrush");
        var muted = (Brush)FindResource("MutedBrush");

        // 가로 그리드 + 가격 라벨
        for (int g = 0; g <= 4; g++)
        {
            double price = _yHi - range * g / 4.0;
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
        DrawEmaLine(Ind.Ema(closes, 20), iLo, iHi, XOf, YOf, Color.FromRgb(0xF0, 0xB9, 0x0B));
        DrawEmaLine(Ind.Ema(closes, 50), iLo, iHi, XOf, YOf, Color.FromRgb(0x4F, 0x9C, 0xF0));
        DrawEmaLine(Ind.Ema(closes, 200), iLo, iHi, XOf, YOf, Color.FromRgb(0xB0, 0x7C, 0xF0));

        // 캔들
        for (int i = iLo; i <= iHi; i++)
        {
            var c = _candles[i];
            double cx = XOf(i);
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
        double last = _candles[count - 1].Close;
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

        // 단타 진입/익절/손절 표시 (회피·보류가 아닐 때, 구체적 진입대기/진입만)
        if (_lastScalp is { } sc && sc.Decision != ScalpDecision.Avoid
            && (sc.Decision == ScalpDecision.Enter || Math.Abs(sc.Entry - last) / last > 0.0015))
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

    private void DrawEmaLine(double[] ema, int iLo, int iHi, Func<double, double> xOf, Func<double, double> yOf, Color color)
    {
        var poly = new Polyline { Stroke = new SolidColorBrush(color), StrokeThickness = 1.3 };
        for (int i = iLo; i <= iHi; i++)
        {
            if (i < 0 || i >= ema.Length) continue;
            double v = ema[i];
            if (double.IsNaN(v)) continue;
            poly.Points.Add(new Point(xOf(i), yOf(v)));
        }
        if (poly.Points.Count > 1) ChartCanvas.Children.Add(poly);
    }

    // ───────────────────────── 차트 팬/줌 ─────────────────────────
    private void ChartCanvas_MouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        int count = _candles.Count;
        if (count < 2 || _plotW <= 0 || _plotH <= 0) return;
        var m = e.GetPosition(ChartCanvas);
        double f = e.Delta > 0 ? 0.85 : 1.18; // 휠 위=확대, 아래=축소

        // 가로 줌 (커서 위치 기준 고정)
        double x1 = (count - 1) - _offsetFromEnd, x0 = x1 - _barsVisible + 1;
        double idxAt = x0 + (m.X / _plotW) * _barsVisible - 0.5;
        double newBars = Math.Clamp(_barsVisible * f, 10, count);
        double fx = Math.Clamp(m.X / _plotW, 0, 1);
        double newX1 = (idxAt - fx * newBars + 0.5) + newBars - 1;
        _offsetFromEnd = (count - 1) - newX1;
        _barsVisible = newBars;

        // 세로 줌 (커서 가격 기준 고정)
        double range = _yHi - _yLo;
        if (range > 0)
        {
            double fy = Math.Clamp((m.Y - _padTop) / _plotH, 0, 1);
            double pAt = _yHi - fy * range;
            double newRange = range * f;
            _yManual = true;
            _yHi = pAt + fy * newRange;
            _yLo = _yHi - newRange;
        }
        DrawChart();
        e.Handled = true;
    }

    private void ChartCanvas_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton != System.Windows.Input.MouseButton.Left) return;
        if (e.ClickCount == 2) { _viewInit = false; _yManual = false; DrawChart(); return; } // 더블클릭 = 초기화
        _dragging = true;
        _dragStart = e.GetPosition(ChartCanvas);
        _dOff0 = _offsetFromEnd; _dBars0 = _barsVisible; _dYLo0 = _yLo; _dYHi0 = _yHi;
        ChartCanvas.CaptureMouse();
    }

    private void ChartCanvas_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_dragging || _plotW <= 0 || _plotH <= 0) return;
        var m = e.GetPosition(ChartCanvas);
        double dx = m.X - _dragStart.X, dy = m.Y - _dragStart.Y;
        // 좌우 팬 (오른쪽으로 끌면 과거 표시)
        _offsetFromEnd = _dOff0 + dx * (_dBars0 / _plotW);
        // 상하 팬
        double range0 = _dYHi0 - _dYLo0;
        if (range0 > 0)
        {
            _yManual = true;
            double ppp = range0 / _plotH;
            _yLo = _dYLo0 + dy * ppp;
            _yHi = _dYHi0 + dy * ppp;
        }
        DrawChart();
    }

    private void ChartCanvas_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _dragging = false;
        if (ChartCanvas.IsMouseCaptured) ChartCanvas.ReleaseMouseCapture();
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

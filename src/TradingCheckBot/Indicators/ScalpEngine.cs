using TradingCheckBot.Models;

namespace TradingCheckBot.Indicators;

/// <summary>단타 상승 시그널 1개의 평가 결과</summary>
public sealed class ScalpSignal
{
    public required string Name { get; init; }
    public required bool Hit { get; init; }
    public required double Score { get; init; }     // 이 시그널이 획득한 점수
    public required double MaxScore { get; init; }   // 만점
    public required string Detail { get; init; }
}

/// <summary>한 심볼의 단타(상승) 종합 분석 결과</summary>
public sealed class ScalpResult
{
    public required string Symbol { get; init; }
    public required string Interval { get; init; }
    public required double Price { get; init; }
    public required DateTime LastTime { get; init; }
    public required int Score { get; init; }          // 0~100 상승 진입 점수
    public required List<ScalpSignal> Signals { get; init; }
    public required double Atr { get; init; }
    public required double Entry { get; init; }
    public required double Target { get; init; }
    public required double Stop { get; init; }

    /// <summary>적중한(상승 근거) 시그널 이름 목록</summary>
    public List<string> Reasons => Signals.Where(s => s.Hit).Select(s => s.Name).ToList();

    public double RiskReward
    {
        get
        {
            double risk = Entry - Stop;
            double reward = Target - Entry;
            return risk <= 0 ? 0 : reward / risk;
        }
    }

    /// <summary>점수 기반 등급</summary>
    public string Grade => Score switch
    {
        >= 75 => "강한 매수",
        >= 60 => "매수 우위",
        >= 45 => "관심",
        _ => "관망"
    };

    public bool IsBuyable => Score >= 60;
}

/// <summary>
/// 단타용 상승 진입 시그널 엔진.
/// 여러 방향(추세·모멘텀·거래량·돌파·되돌림 반등)의 상승 신호를
/// 가중 점수로 합산해 0~100 의 상승 진입 점수를 산출한다.
/// 짧은 주기(1m~15m)에 맞춰 EMA(9/21/50) 등 빠른 설정을 사용한다.
/// </summary>
public static class ScalpEngine
{
    public static ScalpResult Evaluate(string symbol, string interval, IReadOnlyList<Candle> candles)
    {
        var close = candles.Select(c => c.Close).ToArray();
        var high = candles.Select(c => c.High).ToArray();
        var low = candles.Select(c => c.Low).ToArray();
        var vol = candles.Select(c => c.Volume).ToArray();
        int last = candles.Count - 1;
        double price = close[last];

        var ema9 = Ind.Ema(close, 9);
        var ema21 = Ind.Ema(close, 21);
        var ema50 = Ind.Ema(close, 50);
        var rsi = Ind.Rsi(close, 14);
        var macd = Ind.Macd(close);
        var boll = Ind.Bollinger(close, 20, 2);
        var stoch = Ind.Stochastic(high, low, close);
        var adxArr = Ind.Adx(high, low, close);
        var atrArr = Ind.Atr(high, low, close);
        double atr = Get(atrArr, last);
        if (double.IsNaN(atr) || atr <= 0) atr = Math.Max(price * 0.002, 1e-9);

        var signals = new List<ScalpSignal>();

        // ── 방향 1) EMA 단기 정배열 (추세) ──────────────────────────
        {
            double e9 = Get(ema9, last), e21 = Get(ema21, last), e50 = Get(ema50, last);
            bool aligned = e9 > e21 && e21 > e50;
            bool half = e9 > e21;
            double sc = aligned ? 20 : half ? 10 : 0;
            signals.Add(new ScalpSignal
            {
                Name = "EMA 정배열", Hit = sc > 0, Score = sc, MaxScore = 20,
                Detail = aligned ? "EMA 9>21>50 완전 정배열" : half ? "EMA 9>21 단기 우상향" : "정배열 아님"
            });
        }

        // ── 방향 2) 가격이 EMA9 위 + EMA9 상승중 (모멘텀 지속) ──────
        {
            double e9 = Get(ema9, last), e9p = Get(ema9, last - 1);
            bool above = price > e9;
            bool rising = e9 > e9p;
            double sc = (above ? 6 : 0) + (rising ? 6 : 0);
            signals.Add(new ScalpSignal
            {
                Name = "EMA9 모멘텀", Hit = sc > 0, Score = sc, MaxScore = 12,
                Detail = $"가격{(above ? ">" : "<")}EMA9 · EMA9 {(rising ? "상승" : "하락")}중"
            });
        }

        // ── 방향 3) 거래량 폭발 (수급 유입) ─────────────────────────
        {
            double avgVol = AvgVolume(vol, last, 20);
            double ratio = avgVol > 0 ? vol[last] / avgVol : 0;
            double sc = ratio >= 2.5 ? 16 : ratio >= 1.8 ? 12 : ratio >= 1.3 ? 7 : 0;
            signals.Add(new ScalpSignal
            {
                Name = "거래량 폭발", Hit = sc > 0, Score = sc, MaxScore = 16,
                Detail = $"직전20봉 평균대비 {ratio * 100:F0}%"
            });
        }

        // ── 방향 4) RSI 상승 전환 / 과매도 반등 ────────────────────
        {
            double r = Get(rsi, last), rp = Get(rsi, last - 1);
            bool rising = !double.IsNaN(r) && !double.IsNaN(rp) && r > rp;
            double sc = 0; string detail;
            if (rp < 35 && r >= 35 && rising) { sc = 14; detail = $"과매도 반등 {rp:F0}→{r:F0}"; }
            else if (r >= 50 && r < 68 && rising) { sc = 12; detail = $"상승 모멘텀 {r:F0}"; }
            else if (rising && r < 50) { sc = 7; detail = $"회복중 {rp:F0}→{r:F0}"; }
            else if (r >= 68) { sc = 3; detail = $"과열 주의 {r:F0}"; }
            else detail = $"{(double.IsNaN(r) ? "-" : r.ToString("F0"))}";
            signals.Add(new ScalpSignal { Name = "RSI 전환", Hit = sc >= 7, Score = sc, MaxScore = 14, Detail = detail });
        }

        // ── 방향 5) MACD 골든크로스 / 히스토그램 상승 ──────────────
        {
            double m = Get(macd.Macd, last), s = Get(macd.Signal, last);
            double hN = Get(macd.Hist, last), hP = Get(macd.Hist, last - 1);
            bool cross = !double.IsNaN(hP) && hP <= 0 && hN > 0;     // 음→양 전환
            bool histUp = !double.IsNaN(hP) && hN > hP;              // 히스토그램 증가
            bool bull = m > s;
            double sc;
            string detail;
            if (cross) { sc = 15; detail = "히스토그램 음→양 골든크로스"; }
            else if (bull && histUp) { sc = 11; detail = "라인 우위 · 히스토그램 확대"; }
            else if (bull) { sc = 6; detail = "라인 우위"; }
            else { sc = 0; detail = "약세"; }
            signals.Add(new ScalpSignal { Name = "MACD 크로스", Hit = sc > 0, Score = sc, MaxScore = 15, Detail = detail });
        }

        // ── 방향 6) 볼린저 중심선 상향 돌파 / 스퀴즈 확장 ──────────
        {
            double up = Get(boll.Upper, last), mid = Get(boll.Middle, last), lo = Get(boll.Lower, last);
            double sc = 0; string detail = "-";
            if (!double.IsNaN(mid))
            {
                double width = up - lo;
                double widthPrev = Get(boll.Upper, last - 3) - Get(boll.Lower, last - 3);
                bool expanding = !double.IsNaN(widthPrev) && width > widthPrev;   // 변동성 확장
                bool crossedMid = price > mid && Get(close, last - 1) <= mid;
                if (crossedMid && expanding) { sc = 12; detail = "중심선 상향돌파 + 밴드확장"; }
                else if (price > mid && price < up && expanding) { sc = 9; detail = "중심선 위 상승 + 밴드확장"; }
                else if (price > mid) { sc = 5; detail = "중심선 위"; }
                else if (price < lo) { sc = 6; detail = "하단 이탈 — 반등 노림"; }
                else detail = "중심선 아래";
            }
            signals.Add(new ScalpSignal { Name = "볼린저 돌파", Hit = sc > 0, Score = sc, MaxScore = 12, Detail = detail });
        }

        // ── 방향 7) 스토캐스틱 과매도 상향 교차 ────────────────────
        {
            double k = Get(stoch.K, last), d = Get(stoch.D, last);
            double kp = Get(stoch.K, last - 1), dp = Get(stoch.D, last - 1);
            double sc = 0; string detail = "-";
            if (!double.IsNaN(k) && !double.IsNaN(d))
            {
                bool crossUp = kp <= dp && k > d;
                if (k < 30 && crossUp) { sc = 10; detail = $"과매도 상향교차 %K {k:F0}"; }
                else if (crossUp) { sc = 7; detail = $"%K>%D 상향교차 {k:F0}"; }
                else if (k > d && k < 80) { sc = 4; detail = $"%K>%D {k:F0}"; }
                else if (k >= 80) { sc = 1; detail = $"과매수 {k:F0}"; }
                else detail = $"%K {k:F0}";
            }
            signals.Add(new ScalpSignal { Name = "스토캐스틱", Hit = sc >= 7, Score = sc, MaxScore = 10, Detail = detail });
        }

        // ── 방향 8) 직전 고점 돌파 (브레이크아웃) ──────────────────
        {
            int win = 10;
            double priorHigh = double.MinValue;
            for (int i = Math.Max(0, last - win); i < last; i++) priorHigh = Math.Max(priorHigh, high[i]);
            bool breakout = priorHigh > double.MinValue && price > priorHigh;
            double margin = breakout ? (price - priorHigh) / atr : 0;
            double sc = breakout ? (margin >= 0.3 ? 14 : 9) : 0;
            signals.Add(new ScalpSignal
            {
                Name = "고점 돌파", Hit = breakout, Score = sc, MaxScore = 14,
                Detail = breakout ? $"직전 {win}봉 고점 돌파 (+{margin:F1} ATR)" : "박스권 내"
            });
        }

        // ── 방향 9) 눌림목 반등 (EMA21 지지 후 양봉) ───────────────
        {
            double e21 = Get(ema21, last);
            var c = candles[last];
            bool touchedEma = !double.IsNaN(e21) && c.Low <= e21 * 1.001 && c.Close > e21;
            bool bullCandle = c.Close > c.Open;
            double sc = touchedEma && bullCandle ? 10 : 0;
            signals.Add(new ScalpSignal
            {
                Name = "눌림목 반등", Hit = sc > 0, Score = sc, MaxScore = 10,
                Detail = sc > 0 ? "EMA21 지지 후 양봉 반등" : "-"
            });
        }

        // ── 방향 10) 강세 캔들 모멘텀 (몸통/연속 양봉) ─────────────
        {
            var c = candles[last];
            double rangeC = c.High - c.Low;
            double body = c.Close - c.Open;
            bool strongBull = body > 0 && rangeC > 0 && body / rangeC >= 0.55 && c.Close >= c.High - rangeC * 0.25;
            int consec = 0;
            for (int i = last; i >= 0 && candles[i].Close > candles[i].Open; i--) consec++;
            double sc = (strongBull ? 5 : 0) + (consec >= 3 ? 3 : consec >= 2 ? 1.5 : 0);
            signals.Add(new ScalpSignal
            {
                Name = "강세 캔들", Hit = sc > 0, Score = sc, MaxScore = 8,
                Detail = $"{(strongBull ? "장대양봉 " : "")}연속양봉 {consec}개"
            });
        }

        // ── 방향 11) ADX 추세 강화 + 상승 방향 ─────────────────────
        {
            double adx = Get(adxArr, last), adxP = Get(adxArr, last - 1);
            double e9 = Get(ema9, last), e21 = Get(ema21, last);
            bool up = e9 > e21;
            bool strengthening = !double.IsNaN(adxP) && adx > adxP;
            double sc = 0; string detail = "-";
            if (up && adx >= 25 && strengthening) { sc = 8; detail = $"추세 강화 ADX {adx:F0}"; }
            else if (up && adx >= 20) { sc = 5; detail = $"추세 형성 ADX {adx:F0}"; }
            else if (up && strengthening) { sc = 3; detail = $"ADX 상승 {adx:F0}"; }
            else detail = double.IsNaN(adx) ? "-" : $"ADX {adx:F0}";
            signals.Add(new ScalpSignal { Name = "ADX 추세", Hit = sc > 0, Score = sc, MaxScore = 8, Detail = detail });
        }

        // ── 방향 12) 차트 패턴 (상승 반전/지속 패턴 가산) ───────────
        {
            var patterns = PatternEngine.Detect(candles);
            var bullHits = patterns.Where(p => p.Detected && p.Direction == Bias.Bull).ToList();
            var bearHit = patterns.Any(p => p.Detected && p.Direction == Bias.Bear);
            double sc = 0; string detail;
            if (bullHits.Count > 0)
            {
                double best = bullHits.Max(p => p.Confidence);
                sc = Math.Round(12 * best, 1);
                detail = string.Join(", ", bullHits.Select(p => p.Name));
            }
            else if (bearHit) { sc = 0; detail = "하락 패턴 — 매수 보류"; }
            else detail = "패턴 없음";
            signals.Add(new ScalpSignal { Name = "차트패턴", Hit = sc > 0, Score = sc, MaxScore = 12, Detail = detail });
        }

        double achieved = signals.Sum(s => s.Score);
        double max = signals.Sum(s => s.MaxScore);
        int score = (int)Math.Round(100.0 * achieved / max);
        score = Math.Clamp(score, 0, 100);

        // 진입/목표/손절 (ATR 기반, 단타 RR ≈ 1.5)
        double entry = price;
        double stop = entry - atr * 1.0;
        // 직전 스윙 저점이 더 가까우면 그쪽을 손절로 (단, 진입가 아래)
        double swingLow = double.MaxValue;
        for (int i = Math.Max(0, last - 6); i <= last; i++) swingLow = Math.Min(swingLow, low[i]);
        if (swingLow < entry && swingLow > stop) stop = swingLow - atr * 0.1;
        double risk = entry - stop;
        double target = entry + risk * 1.5;

        return new ScalpResult
        {
            Symbol = symbol,
            Interval = interval,
            Price = price,
            LastTime = candles[last].OpenTime,
            Score = score,
            Signals = signals,
            Atr = atr,
            Entry = entry,
            Target = target,
            Stop = stop
        };
    }

    private static double Get(double[] arr, int i) => i >= 0 && i < arr.Length ? arr[i] : double.NaN;

    private static double AvgVolume(double[] vol, int last, int period)
    {
        int start = Math.Max(0, last - period);
        int cnt = last - start; // 마지막(현재) 봉 제외
        if (cnt <= 0) return 0;
        double sum = 0;
        for (int i = start; i < last; i++) sum += vol[i];
        return sum / cnt;
    }
}

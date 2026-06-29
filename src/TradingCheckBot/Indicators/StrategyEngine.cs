using TradingCheckBot.Models;

namespace TradingCheckBot.Indicators;

public enum Bias { Bull = 1, Neutral = 0, Bear = -1 }

/// <summary>지표 1개의 판정 결과</summary>
public sealed class IndicatorSignal
{
    public required string Name { get; init; }
    public required string Value { get; init; }
    public required Bias Direction { get; init; }
    public required string Comment { get; init; }
}

/// <summary>전체 종합 판정 결과</summary>
public sealed class SignalResult
{
    public required string Symbol { get; init; }
    public required string Interval { get; init; }
    public required double LastPrice { get; init; }
    public required DateTime LastTime { get; init; }
    public required List<IndicatorSignal> Signals { get; init; }
    public required int BullCount { get; init; }
    public required int BearCount { get; init; }
    public required int NeutralCount { get; init; }
    public required double AdxValue { get; init; }

    /// <summary>상방 점수 - 하방 점수 (양수=상방 우위)</summary>
    public int NetScore => BullCount - BearCount;

    /// <summary>0~100 상방 확률 형태로 환산한 강도</summary>
    public int BullPercent
    {
        get
        {
            int total = BullCount + BearCount;
            if (total == 0) return 50;
            return (int)Math.Round(100.0 * BullCount / total);
        }
    }

    public Bias Verdict
    {
        get
        {
            int net = NetScore;
            if (net >= 2) return Bias.Bull;
            if (net <= -2) return Bias.Bear;
            return Bias.Neutral;
        }
    }

    public string VerdictText => Verdict switch
    {
        Bias.Bull => "상방 (LONG)",
        Bias.Bear => "하방 (SHORT)",
        _ => "중립 (관망)"
    };

    /// <summary>추세 강도 라벨 (ADX 기반)</summary>
    public string TrendStrength => AdxValue switch
    {
        >= 40 => "매우 강함",
        >= 25 => "강함",
        >= 20 => "보통",
        _ => "약함/횡보"
    };
}

/// <summary>
/// 여러 보조지표를 종합해 상방/하방 신호를 점수화한다.
/// 각 지표는 +1(상방) / 0(중립) / -1(하방) 으로 투표한다.
/// </summary>
public static class StrategyEngine
{
    public static SignalResult Evaluate(string symbol, string interval, IReadOnlyList<Candle> candles)
    {
        var close = candles.Select(c => c.Close).ToArray();
        var high = candles.Select(c => c.High).ToArray();
        var low = candles.Select(c => c.Low).ToArray();
        int last = candles.Count - 1;
        double price = close[last];

        var ema20 = Ind.Ema(close, 20);
        var ema50 = Ind.Ema(close, 50);
        var ema200 = Ind.Ema(close, 200);
        var rsi = Ind.Rsi(close, 14);
        var macd = Ind.Macd(close);
        var boll = Ind.Bollinger(close, 20, 2);
        var stoch = Ind.Stochastic(high, low, close);
        var adx = Ind.Adx(high, low, close);

        var signals = new List<IndicatorSignal>();

        // 1) 가격 vs EMA200 (장기 추세)
        signals.Add(Compare("가격 vs EMA200(장기추세)",
            price, ema200[last],
            $"가격 {Fmt(price)} / EMA200 {FmtN(ema200[last])}",
            bull: "장기 상승 추세권", bear: "장기 하락 추세권"));

        // 2) EMA20 vs EMA50 (단기 정배열)
        signals.Add(Compare("EMA20 vs EMA50",
            ema20[last], ema50[last],
            $"EMA20 {FmtN(ema20[last])} / EMA50 {FmtN(ema50[last])}",
            bull: "단기 정배열", bear: "단기 역배열"));

        // 3) EMA50 vs EMA200 (중기 정배열 / 골든·데드 영역)
        signals.Add(Compare("EMA50 vs EMA200",
            ema50[last], ema200[last],
            $"EMA50 {FmtN(ema50[last])} / EMA200 {FmtN(ema200[last])}",
            bull: "중기 정배열(골든 영역)", bear: "중기 역배열(데드 영역)"));

        // 4) RSI
        signals.Add(RsiSignal(rsi[last]));

        // 5) MACD (라인 vs 시그널 + 히스토그램)
        signals.Add(MacdSignal(macd.Macd[last], macd.Signal[last], macd.Hist[last]));

        // 6) 볼린저밴드 위치
        signals.Add(BollSignal(price, boll.Upper[last], boll.Middle[last], boll.Lower[last]));

        // 7) 스토캐스틱
        signals.Add(StochSignal(stoch.K[last], stoch.D[last]));

        int bull = signals.Count(s => s.Direction == Bias.Bull);
        int bear = signals.Count(s => s.Direction == Bias.Bear);
        int neu = signals.Count(s => s.Direction == Bias.Neutral);

        return new SignalResult
        {
            Symbol = symbol,
            Interval = interval,
            LastPrice = price,
            LastTime = candles[last].OpenTime,
            Signals = signals,
            BullCount = bull,
            BearCount = bear,
            NeutralCount = neu,
            AdxValue = double.IsNaN(adx[last]) ? 0 : adx[last]
        };
    }

    private static IndicatorSignal Compare(string name, double a, double b, string value, string bull, string bear)
    {
        if (double.IsNaN(a) || double.IsNaN(b))
            return new IndicatorSignal { Name = name, Value = "데이터 부족", Direction = Bias.Neutral, Comment = "캔들 수 부족" };
        var dir = a > b ? Bias.Bull : a < b ? Bias.Bear : Bias.Neutral;
        return new IndicatorSignal { Name = name, Value = value, Direction = dir, Comment = dir == Bias.Bull ? bull : dir == Bias.Bear ? bear : "동일" };
    }

    private static IndicatorSignal RsiSignal(double rsi)
    {
        if (double.IsNaN(rsi))
            return new IndicatorSignal { Name = "RSI(14)", Value = "데이터 부족", Direction = Bias.Neutral, Comment = "캔들 수 부족" };
        Bias dir;
        string comment;
        if (rsi >= 70) { dir = Bias.Bear; comment = "과매수 — 하방 반전 주의"; }
        else if (rsi <= 30) { dir = Bias.Bull; comment = "과매도 — 상방 반등 가능"; }
        else if (rsi >= 55) { dir = Bias.Bull; comment = "상방 모멘텀 우위"; }
        else if (rsi <= 45) { dir = Bias.Bear; comment = "하방 모멘텀 우위"; }
        else { dir = Bias.Neutral; comment = "중립 구간"; }
        return new IndicatorSignal { Name = "RSI(14)", Value = rsi.ToString("F1"), Direction = dir, Comment = comment };
    }

    private static IndicatorSignal MacdSignal(double macd, double sig, double hist)
    {
        if (double.IsNaN(macd) || double.IsNaN(sig))
            return new IndicatorSignal { Name = "MACD(12,26,9)", Value = "데이터 부족", Direction = Bias.Neutral, Comment = "캔들 수 부족" };
        Bias dir = macd > sig ? Bias.Bull : macd < sig ? Bias.Bear : Bias.Neutral;
        string comment = dir == Bias.Bull
            ? (hist > 0 ? "골든크로스 · 히스토그램(+)" : "라인 우위")
            : dir == Bias.Bear ? (hist < 0 ? "데드크로스 · 히스토그램(-)" : "라인 열위") : "교차 없음";
        return new IndicatorSignal { Name = "MACD(12,26,9)", Value = $"{macd:F2} / {sig:F2}", Direction = dir, Comment = comment };
    }

    private static IndicatorSignal BollSignal(double price, double up, double mid, double lo)
    {
        if (double.IsNaN(mid))
            return new IndicatorSignal { Name = "볼린저밴드(20,2)", Value = "데이터 부족", Direction = Bias.Neutral, Comment = "캔들 수 부족" };
        Bias dir;
        string comment;
        if (price > up) { dir = Bias.Bear; comment = "상단 돌파 — 과열/되돌림 주의"; }
        else if (price < lo) { dir = Bias.Bull; comment = "하단 이탈 — 반등 가능"; }
        else if (price > mid) { dir = Bias.Bull; comment = "중심선 위 — 상방 우위"; }
        else if (price < mid) { dir = Bias.Bear; comment = "중심선 아래 — 하방 우위"; }
        else { dir = Bias.Neutral; comment = "중심선 부근"; }
        return new IndicatorSignal { Name = "볼린저밴드(20,2)", Value = $"중심 {FmtN(mid)}", Direction = dir, Comment = comment };
    }

    private static IndicatorSignal StochSignal(double k, double d)
    {
        if (double.IsNaN(k) || double.IsNaN(d))
            return new IndicatorSignal { Name = "스토캐스틱(14,3,3)", Value = "데이터 부족", Direction = Bias.Neutral, Comment = "캔들 수 부족" };
        Bias dir;
        string comment;
        if (k >= 80) { dir = Bias.Bear; comment = "과매수 구간"; }
        else if (k <= 20) { dir = Bias.Bull; comment = "과매도 구간"; }
        else if (k > d) { dir = Bias.Bull; comment = "%K>%D 상방 교차"; }
        else if (k < d) { dir = Bias.Bear; comment = "%K<%D 하방 교차"; }
        else { dir = Bias.Neutral; comment = "중립"; }
        return new IndicatorSignal { Name = "스토캐스틱(14,3,3)", Value = $"%K {k:F0} / %D {d:F0}", Direction = dir, Comment = comment };
    }

    private static string Fmt(double v) => v >= 100 ? v.ToString("N1") : v.ToString("0.######");
    private static string FmtN(double v) => double.IsNaN(v) ? "-" : (v >= 100 ? v.ToString("N1") : v.ToString("0.######"));
}

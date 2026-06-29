using TradingCheckBot.Models;

namespace TradingCheckBot.Indicators;

/// <summary>매매 방향</summary>
public enum TradeSide { Long, Short }

/// <summary>과거 유사구간 통계 + 셋업 기반 양방향(롱/숏) 예측 결과</summary>
public sealed class PredictResult
{
    public required string Symbol { get; init; }
    public required string Interval { get; init; }
    public required double Price { get; init; }
    public required DateTime LastTime { get; init; }

    /// <summary>기대값이 더 높은 방향</summary>
    public required TradeSide Side { get; init; }
    /// <summary>해당 방향 예상 변동률 % (유리한 방향 평균 최대 변동 + 셋업 보정, 양수)</summary>
    public required double ExpectedMovePct { get; init; }
    /// <summary>해당 방향 승률 %</summary>
    public required double WinRate { get; init; }
    /// <summary>순 기대수익률 % (방향 반영, 양수=유리)</summary>
    public required double ExpectedNetPct { get; init; }
    public required int Samples { get; init; }
    public required string Setup { get; init; }
    public required bool HasSetup { get; init; }

    public required double Entry { get; init; }
    public required double Target { get; init; }
    public required double Stop { get; init; }

    /// <summary>랭킹용 종합 점수 (확률가중 기대변동 + 셋업)</summary>
    public required double Score { get; init; }
    public required List<string> Reasons { get; init; }

    public string SideText => Side == TradeSide.Long ? "롱" : "숏";

    public double RiskReward
    {
        get
        {
            double risk = Side == TradeSide.Long ? Entry - Stop : Stop - Entry;
            double reward = Side == TradeSide.Long ? Target - Entry : Entry - Target;
            return risk <= 0 ? 0 : reward / risk;
        }
    }

    public string Tier => Score switch
    {
        >= 65 => "유망",
        >= 45 => "관심",
        >= 25 => "보통",
        _ => "낮음"
    };
}

/// <summary>
/// 양방향(롱/숏) 상승·하락 예측 엔진 — "과거 차트 통계 + 셋업" 결합.
/// 코인의 과거 데이터에서 현재와 비슷한 구간(KNN)을 찾아 이후 H봉의 상승/하락 분포를 통계낸 뒤,
/// 롱 기대값(평균 최대상승×상승확률)과 숏 기대값(평균 최대하락×하락확률)을 비교해 유리한 방향을 고른다.
/// 사전 셋업(스퀴즈/상승·하락 패턴/추세 눌림)이면 해당 방향에 가산한다.
/// </summary>
public static class PredictEngine
{
    private const int Horizon = 10;
    private const int Warmup = 60;
    private const int MaxNeighbors = 40;

    public static PredictResult Predict(string symbol, string interval, IReadOnlyList<Candle> candles)
    {
        // 진행 중(미완성) 봉 제외 → 확정봉 기준 예측 (같은 봉 동안 결과 안정)
        if (candles.Count > 120) candles = candles.Take(candles.Count - 1).ToList();

        int n = candles.Count;
        var close = candles.Select(c => c.Close).ToArray();
        var high = candles.Select(c => c.High).ToArray();
        var low = candles.Select(c => c.Low).ToArray();
        var vol = candles.Select(c => c.Volume).ToArray();
        double price = close[n - 1];

        var ema9 = Ind.Ema(close, 9);
        var ema20 = Ind.Ema(close, 20);
        var ema50 = Ind.Ema(close, 50);
        var rsi = Ind.Rsi(close, 14);
        var stoch = Ind.Stochastic(high, low, close);
        var atrArr = Ind.Atr(high, low, close);
        var boll = Ind.Bollinger(close, 20, 2);
        double atr = atrArr[n - 1]; if (double.IsNaN(atr) || atr <= 0) atr = price * 0.002;

        var reasons = new List<string>();
        var (bullSetup, bullBonus, bearSetup, bearBonus) = DetectSetup(candles, close, high, low, vol, ema20, ema50, boll, atrArr);

        // 특징 벡터
        var feats = new List<double[]>();
        var fIdx = new List<int>();
        for (int i = Warmup; i <= n - 1; i++)
        {
            var f = Feature(i, close, high, low, vol, ema9, ema20, ema50, rsi, stoch, atrArr, boll);
            if (f == null) continue;
            feats.Add(f); fIdx.Add(i);
        }

        if (feats.Count < 40 || n < Warmup + Horizon + 20)
            return Fallback(symbol, interval, price, candles[n - 1].OpenTime, bullSetup, bullBonus, bearSetup, bearBonus, reasons, atr);

        int dim = feats[0].Length;
        var mean = new double[dim]; var std = new double[dim];
        foreach (var f in feats) for (int j = 0; j < dim; j++) mean[j] += f[j];
        for (int j = 0; j < dim; j++) mean[j] /= feats.Count;
        foreach (var f in feats) for (int j = 0; j < dim; j++) std[j] += (f[j] - mean[j]) * (f[j] - mean[j]);
        for (int j = 0; j < dim; j++) std[j] = Math.Sqrt(std[j] / feats.Count) + 1e-9;
        double[] Z(double[] f) { var z = new double[dim]; for (int j = 0; j < dim; j++) z[j] = (f[j] - mean[j]) / std[j]; return z; }
        var cur = Z(feats[^1]);

        var cand = new List<(double dist, double fwd, double up, double dn)>();
        for (int t = 0; t < feats.Count; t++)
        {
            int i = fIdx[t];
            if (i > n - 1 - Horizon) continue;
            var z = Z(feats[t]);
            double dist = 0; for (int j = 0; j < dim; j++) { double dd = z[j] - cur[j]; dist += dd * dd; }
            double c0 = close[i];
            double fwd = (close[i + Horizon] - c0) / c0;
            double hi = double.MinValue, lo = double.MaxValue;
            for (int h = i + 1; h <= i + Horizon; h++) { hi = Math.Max(hi, high[h]); lo = Math.Min(lo, low[h]); }
            cand.Add((dist, fwd, (hi - c0) / c0, (lo - c0) / c0));
        }
        if (cand.Count < 20)
            return Fallback(symbol, interval, price, candles[n - 1].OpenTime, bullSetup, bullBonus, bearSetup, bearBonus, reasons, atr);

        int K = Math.Min(MaxNeighbors, Math.Max(15, cand.Count / 8));
        var nn = cand.OrderBy(c => c.dist).Take(K).ToList();

        double avgFwd = nn.Average(x => x.fwd);
        double avgUp = nn.Average(x => x.up) * 100;          // 평균 최대상승 %
        double avgDn = -nn.Average(x => x.dn) * 100;         // 평균 최대하락 % (양수 크기)
        double pUp = 100.0 * nn.Count(x => x.fwd > 0.001) / nn.Count;
        double pDn = 100.0 * nn.Count(x => x.fwd < -0.001) / nn.Count;

        // 방향별 확률가중 기대값
        double longEV = avgUp * (pUp / 100.0) + bullBonus * 0.4;
        double shortEV = avgDn * (pDn / 100.0) + bearBonus * 0.4;

        TradeSide side; double movePct, winRate, netPct, bonus; string setup;
        if (longEV >= shortEV)
        {
            side = TradeSide.Long; movePct = avgUp + bullBonus * 0.5; winRate = pUp;
            netPct = avgFwd * 100; bonus = bullBonus; setup = bullSetup;
        }
        else
        {
            side = TradeSide.Short; movePct = avgDn + bearBonus * 0.5; winRate = pDn;
            netPct = -avgFwd * 100; bonus = bearBonus; setup = bearSetup;
        }

        double ev = Math.Max(longEV, shortEV);
        double score = Math.Clamp(ev * 6.0 + bonus + (winRate - 50) * 0.3, 0, 100);

        reasons.Insert(0, $"유사사례 {K}건: 평균 최대{(side == TradeSide.Long ? "상승" : "하락")} {movePct:F1}% · 승률 {winRate:F0}% · 순기대 {netPct:+0.0;-0.0}%");
        if (bonus > 0) reasons.Add($"셋업: {setup}");

        // 진입/목표/손절 (방향별)
        double entry = price, target, stop;
        if (side == TradeSide.Long)
        {
            target = price * (1 + Math.Max(avgUp, 0.3) / 100.0);
            double swingLow = double.MaxValue; for (int i = Math.Max(0, n - 6); i < n; i++) swingLow = Math.Min(swingLow, low[i]);
            stop = Math.Min(swingLow, price * (1 - Math.Max(avgDn, 0.1) / 100.0)) - atr * 0.1;
        }
        else
        {
            target = price * (1 - Math.Max(avgDn, 0.3) / 100.0);
            double swingHigh = double.MinValue; for (int i = Math.Max(0, n - 6); i < n; i++) swingHigh = Math.Max(swingHigh, high[i]);
            stop = Math.Max(swingHigh, price * (1 + Math.Max(avgUp, 0.1) / 100.0)) + atr * 0.1;
        }

        return new PredictResult
        {
            Symbol = symbol, Interval = interval, Price = price, LastTime = candles[n - 1].OpenTime,
            Side = side, ExpectedMovePct = movePct, WinRate = winRate, ExpectedNetPct = netPct,
            Samples = K, Setup = setup, HasSetup = bonus > 0,
            Entry = entry, Target = target, Stop = stop, Score = score, Reasons = reasons
        };
    }

    private static double[]? Feature(int i, double[] close, double[] high, double[] low, double[] vol,
        double[] ema9, double[] ema20, double[] ema50, double[] rsi, Ind.StochResult stoch, double[] atr, Ind.BollResult boll)
    {
        if (i < 25) return null;
        double a = atr[i]; if (double.IsNaN(a) || a <= 0) return null;
        double e9 = ema9[i], e20 = ema20[i], e50 = ema50[i], rv = rsi[i], kk = stoch.K[i];
        if (double.IsNaN(e9) || double.IsNaN(e20) || double.IsNaN(e50) || double.IsNaN(rv) || double.IsNaN(kk)) return null;
        double bw = boll.Upper[i] - boll.Lower[i];
        if (double.IsNaN(bw)) return null;

        double mn = double.MaxValue, mx = double.MinValue;
        for (int j = i - 19; j <= i; j++) { mn = Math.Min(mn, low[j]); mx = Math.Max(mx, high[j]); }
        double pos = mx > mn ? (close[i] - mn) / (mx - mn) : 0.5;
        double avgVol = 0; int vc = 0; for (int j = i - 19; j < i; j++) { avgVol += vol[j]; vc++; }
        avgVol = vc > 0 ? avgVol / vc : 0;
        double volR = avgVol > 0 ? vol[i] / avgVol : 1;

        return new[]
        {
            rv / 100.0, kk / 100.0,
            (close[i] - e20) / a,
            (e9 - ema9[i - 3]) / a,
            (e20 - ema20[i - 5]) / a,
            (e9 - e50) / a,
            pos, a / close[i] * 100,
            Math.Min(volR, 4),
            (close[i] - close[i - 5]) / a,
            bw / e20 * 100
        };
    }

    // 롱/숏 양쪽 셋업 탐지
    private static (string bullSetup, double bullBonus, string bearSetup, double bearBonus) DetectSetup(
        IReadOnlyList<Candle> candles, double[] close, double[] high, double[] low, double[] vol,
        double[] ema20, double[] ema50, Ind.BollResult boll, double[] atr)
    {
        int n = close.Length; int last = n - 1; double price = close[last];
        double bullB = 0, bearB = 0; string bull = "—", bear = "—";

        // 볼린저 스퀴즈 (양방향 — 변동성 수축 → 돌파 임박)
        double bw = boll.Upper[last] - boll.Lower[last];
        bool squeeze = false;
        if (!double.IsNaN(bw))
        {
            var widths = new List<double>();
            for (int i = Math.Max(20, last - 100); i <= last; i++) { double w = boll.Upper[i] - boll.Lower[i]; if (!double.IsNaN(w)) widths.Add(w); }
            if (widths.Count > 20) { widths.Sort(); if (bw <= widths[(int)(widths.Count * 0.25)]) squeeze = true; }
        }
        if (squeeze) { bullB += 8; bearB += 8; bull = "볼린저 스퀴즈"; bear = "볼린저 스퀴즈"; }

        // 패턴 (상승/하락)
        var pats = PatternEngine.Detect(candles);
        var bullP = pats.Where(p => p.Detected && p.Direction == Bias.Bull).ToList();
        var bearP = pats.Where(p => p.Detected && p.Direction == Bias.Bear).ToList();
        if (bullP.Count > 0) { bullB += 10 * bullP.Max(p => p.Confidence); bull = string.Join(",", bullP.Select(p => p.Name)); }
        if (bearP.Count > 0) { bearB += 10 * bearP.Max(p => p.Confidence); bear = string.Join(",", bearP.Select(p => p.Name)); }

        // 추세 정렬 가산
        double e20 = ema20[last], e50 = ema50[last], a = atr[last];
        if (!double.IsNaN(e20) && !double.IsNaN(e50) && a > 0)
        {
            double dist = (price - e20) / a;
            if (e20 > e50 && dist > -0.5 && dist < 1.0) { bullB += 6; if (bull == "—") bull = "상승추세 눌림"; }
            if (e20 < e50 && dist < 0.5 && dist > -1.0) { bearB += 6; if (bear == "—") bear = "하락추세 되돌림"; }
        }

        return (bull, Math.Min(bullB, 28), bear, Math.Min(bearB, 28));
    }

    private static PredictResult Fallback(string sym, string itv, double price, DateTime t,
        string bullSetup, double bullBonus, string bearSetup, double bearBonus, List<string> reasons, double atr)
    {
        if (double.IsNaN(atr) || atr <= 0) atr = price * 0.002;
        bool longSide = bullBonus >= bearBonus;
        double bonus = longSide ? bullBonus : bearBonus;
        if (reasons.Count == 0) reasons.Add("과거 표본 부족 — 셋업 기준 약식 판정");
        return new PredictResult
        {
            Symbol = sym, Interval = itv, Price = price, LastTime = t,
            Side = longSide ? TradeSide.Long : TradeSide.Short,
            ExpectedMovePct = bonus * 0.2, WinRate = 0, ExpectedNetPct = 0,
            Samples = 0, Setup = longSide ? bullSetup : bearSetup, HasSetup = bonus > 0,
            Entry = price,
            Target = longSide ? price + atr * 2 : price - atr * 2,
            Stop = longSide ? price - atr * 1.2 : price + atr * 1.2,
            Score = Math.Clamp(bonus, 0, 60), Reasons = reasons
        };
    }
}

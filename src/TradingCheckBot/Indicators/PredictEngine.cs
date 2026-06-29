using TradingCheckBot.Models;

namespace TradingCheckBot.Indicators;

/// <summary>과거 유사구간 통계 + 셋업 기반 상승 예측 결과</summary>
public sealed class PredictResult
{
    public required string Symbol { get; init; }
    public required string Interval { get; init; }
    public required double Price { get; init; }
    public required DateTime LastTime { get; init; }

    /// <summary>예상 상승률 % (유사 과거구간들의 이후 H봉 평균 최대상승 + 셋업 보정)</summary>
    public required double ExpectedRisePct { get; init; }
    /// <summary>순(net) 기대수익률 % (이후 H봉 종가 기준 평균)</summary>
    public required double ExpectedNetPct { get; init; }
    /// <summary>예상 하락(손실) % (이후 H봉 평균 최대낙폭, 음수)</summary>
    public required double ExpectedDrawPct { get; init; }
    /// <summary>승률 % (이후 양의 수익 비율)</summary>
    public required double WinRate { get; init; }
    /// <summary>유사 사례 수 (신뢰도)</summary>
    public required int Samples { get; init; }
    /// <summary>탐지된 사전 셋업</summary>
    public required string Setup { get; init; }
    public required bool HasSetup { get; init; }

    public required double Entry { get; init; }
    public required double Target { get; init; }
    public required double Stop { get; init; }

    /// <summary>랭킹용 종합 점수 (확률가중 기대상승 + 셋업)</summary>
    public required double Score { get; init; }
    public required List<string> Reasons { get; init; }

    public double RiskReward
    {
        get
        {
            double risk = Entry - Stop, reward = Target - Entry;
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
/// 상승 예측 엔진 — "과거 차트 통계 + 셋업" 결합.
/// 1) 코인의 과거 데이터에서 현재와 가장 비슷한 구간(KNN)을 찾아
///    그 이후 H봉의 평균 상승률·승률·낙폭을 통계로 구한다(데이터 기반 예측).
/// 2) 돌파 직전 축적 셋업(볼린저 스퀴즈·바닥다지기·상승 패턴)이면 가산한다.
/// → 아직 안 오른 '관망/축적' 구간에서 곧 오를 가능성이 높은 종목을 예상 상승률 순으로 찾는다.
/// </summary>
public static class PredictEngine
{
    private const int Horizon = 10;     // 예측 구간(봉 수)
    private const int Warmup = 60;       // 지표 안정화 구간
    private const int MaxNeighbors = 40; // 최대 유사 사례 수

    public static PredictResult Predict(string symbol, string interval, IReadOnlyList<Candle> candles)
    {
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
        var atr = Ind.Atr(high, low, close);
        var boll = Ind.Bollinger(close, 20, 2);

        // ── 셋업(사전 축적) 탐지 ────────────────────────────────────
        var reasons = new List<string>();
        var (setup, hasSetup, setupBonus) = DetectSetup(candles, close, high, low, vol, ema9, ema20, ema50, rsi, boll, atr, reasons);

        // ── 특징 벡터 빌드 (스케일 불변) ────────────────────────────
        var feats = new List<double[]>();
        var fIdx = new List<int>();
        for (int i = Warmup; i <= n - 1; i++)
        {
            var f = Feature(i, close, high, low, vol, ema9, ema20, ema50, rsi, stoch, atr, boll);
            if (f == null) continue;
            feats.Add(f); fIdx.Add(i);
        }

        // 데이터 부족 → 셋업만으로 약식 결과
        if (feats.Count < 40 || n < Warmup + Horizon + 20)
            return Fallback(symbol, interval, price, candles[n - 1].OpenTime, setup, hasSetup, setupBonus, reasons, atr[n - 1]);

        int dim = feats[0].Length;
        // 표준화 (z-score)
        var mean = new double[dim]; var std = new double[dim];
        foreach (var f in feats) for (int j = 0; j < dim; j++) mean[j] += f[j];
        for (int j = 0; j < dim; j++) mean[j] /= feats.Count;
        foreach (var f in feats) for (int j = 0; j < dim; j++) std[j] += (f[j] - mean[j]) * (f[j] - mean[j]);
        for (int j = 0; j < dim; j++) std[j] = Math.Sqrt(std[j] / feats.Count) + 1e-9;

        double[] Z(double[] f) { var z = new double[dim]; for (int j = 0; j < dim; j++) z[j] = (f[j] - mean[j]) / std[j]; return z; }

        var cur = Z(feats[^1]); // 현재(마지막) 구간

        // ── 과거 유사구간 거리 계산 (forward 가능한 구간만) ─────────
        var cand = new List<(double dist, double fwd, double up, double dn)>();
        for (int t = 0; t < feats.Count; t++)
        {
            int i = fIdx[t];
            if (i > n - 1 - Horizon) continue; // 미래 데이터 없는 최근 구간 제외
            var z = Z(feats[t]);
            double dist = 0; for (int j = 0; j < dim; j++) { double dd = z[j] - cur[j]; dist += dd * dd; }

            double c0 = close[i];
            double fwd = (close[i + Horizon] - c0) / c0;
            double hi = double.MinValue, lo = double.MaxValue;
            for (int h = i + 1; h <= i + Horizon; h++) { hi = Math.Max(hi, high[h]); lo = Math.Min(lo, low[h]); }
            double up = (hi - c0) / c0;
            double dn = (lo - c0) / c0;
            cand.Add((dist, fwd, up, dn));
        }
        if (cand.Count < 20)
            return Fallback(symbol, interval, price, candles[n - 1].OpenTime, setup, hasSetup, setupBonus, reasons, atr[n - 1]);

        int K = Math.Min(MaxNeighbors, Math.Max(15, cand.Count / 8));
        var nn = cand.OrderBy(c => c.dist).Take(K).ToList();

        double expNet = nn.Average(x => x.fwd) * 100;
        double expUp = nn.Average(x => x.up) * 100;
        double expDn = nn.Average(x => x.dn) * 100;
        double winRate = 100.0 * nn.Count(x => x.fwd > 0.001) / nn.Count;

        // 예상 상승률 = 평균 최대상승 + 셋업 보정(있으면 약간 상향)
        double expRise = expUp * (hasSetup ? 1.0 : 1.0) + setupBonus * 0.5;

        // 종합 점수 = 확률가중 기대상승 + 셋업 + 승률 보너스
        double ev = expUp * (winRate / 100.0);            // 확률가중 기대상승
        double score = ev * 6.0 + setupBonus + (winRate - 50) * 0.3;
        score = Math.Clamp(score, 0, 100);

        reasons.Insert(0, $"유사사례 {K}건: 평균 최대상승 +{expUp:F1}% · 순수익 {expNet:+0.0;-0.0}% · 승률 {winRate:F0}%");

        // 진입/목표/손절 (예측 통계 기반)
        double atrV = atr[n - 1]; if (double.IsNaN(atrV) || atrV <= 0) atrV = price * 0.002;
        double entry = price;
        double target = price * (1 + Math.Max(expUp, 0.3) / 100.0);
        double swingLow = double.MaxValue; for (int i = Math.Max(0, n - 6); i < n; i++) swingLow = Math.Min(swingLow, low[i]);
        double statStop = price * (1 + Math.Min(expDn, -0.1) / 100.0);
        double stop = Math.Min(swingLow, statStop) - atrV * 0.1;

        return new PredictResult
        {
            Symbol = symbol,
            Interval = interval,
            Price = price,
            LastTime = candles[n - 1].OpenTime,
            ExpectedRisePct = expRise,
            ExpectedNetPct = expNet,
            ExpectedDrawPct = expDn,
            WinRate = winRate,
            Samples = K,
            Setup = setup,
            HasSetup = hasSetup,
            Entry = entry,
            Target = target,
            Stop = stop,
            Score = score,
            Reasons = reasons
        };
    }

    // ───────────────────────── 특징 벡터 ─────────────────────────
    private static double[]? Feature(int i, double[] close, double[] high, double[] low, double[] vol,
        double[] ema9, double[] ema20, double[] ema50, double[] rsi, Ind.StochResult stoch, double[] atr, Ind.BollResult boll)
    {
        if (i < 25) return null;
        double a = atr[i]; if (double.IsNaN(a) || a <= 0) return null;
        double e9 = ema9[i], e20 = ema20[i], e50 = ema50[i], r = rsi[i], kk = stoch.K[i];
        if (double.IsNaN(e9) || double.IsNaN(e20) || double.IsNaN(e50) || double.IsNaN(r) || double.IsNaN(kk)) return null;
        double bw = (boll.Upper[i] - boll.Lower[i]);
        if (double.IsNaN(bw)) return null;

        double mn = double.MaxValue, mx = double.MinValue;
        for (int j = i - 19; j <= i; j++) { mn = Math.Min(mn, low[j]); mx = Math.Max(mx, high[j]); }
        double pos = mx > mn ? (close[i] - mn) / (mx - mn) : 0.5;

        double avgVol = 0; int vc = 0; for (int j = i - 19; j < i; j++) { avgVol += vol[j]; vc++; }
        avgVol = vc > 0 ? avgVol / vc : 0;
        double volR = avgVol > 0 ? vol[i] / avgVol : 1;

        return new[]
        {
            r / 100.0,                                  // RSI
            kk / 100.0,                                 // 스토캐스틱 %K
            (close[i] - e20) / a,                       // EMA20 이격(ATR)
            (e9 - ema9[i - 3]) / a,                      // EMA9 기울기
            (e20 - ema20[i - 5]) / a,                    // EMA20 기울기
            (e9 - e50) / a,                              // 단·장기 정렬
            pos,                                        // 최근20봉 내 위치
            a / close[i] * 100,                         // 변동성(ATR%)
            Math.Min(volR, 4),                          // 거래량 비율
            (close[i] - close[i - 5]) / a,               // 단기 모멘텀
            bw / e20 * 100                              // 볼린저 폭(스퀴즈)
        };
    }

    // ───────────────────────── 셋업(사전 축적) 탐지 ─────────────────────────
    private static (string setup, bool has, double bonus) DetectSetup(
        IReadOnlyList<Candle> candles, double[] close, double[] high, double[] low, double[] vol,
        double[] ema9, double[] ema20, double[] ema50, double[] rsi, Ind.BollResult boll, double[] atr, List<string> reasons)
    {
        int n = close.Length;
        int last = n - 1;
        double price = close[last];
        double bonus = 0; string setup = "—"; bool has = false;

        // 1) 볼린저 스퀴즈: 현재 밴드폭이 최근 100봉 중 하위 25%
        double bw = boll.Upper[last] - boll.Lower[last];
        if (!double.IsNaN(bw))
        {
            var widths = new List<double>();
            for (int i = Math.Max(20, last - 100); i <= last; i++)
            {
                double w = boll.Upper[i] - boll.Lower[i];
                if (!double.IsNaN(w)) widths.Add(w);
            }
            if (widths.Count > 20)
            {
                widths.Sort();
                double q25 = widths[(int)(widths.Count * 0.25)];
                if (bw <= q25) { setup = "볼린저 스퀴즈(변동성 수축)"; has = true; bonus += 12; reasons.Add("볼린저 스퀴즈 — 변동성 수축, 돌파 임박"); }
            }
        }

        // 2) 상승 패턴 (바닥/축적형)
        var bull = PatternEngine.Detect(candles).Where(p => p.Detected && p.Direction == Bias.Bull).ToList();
        if (bull.Count > 0)
        {
            string names = string.Join(", ", bull.Select(p => p.Name));
            if (!has) setup = names;
            has = true; bonus += 10 * bull.Max(p => p.Confidence);
            reasons.Add($"📐 상승 패턴: {names}");
        }

        // 3) 눌림 지지: 상승추세에서 EMA20/50 부근 지지 (아직 과확장 아님)
        double e20 = ema20[last], e50 = ema50[last], a = atr[last];
        if (!double.IsNaN(e20) && !double.IsNaN(e50) && a > 0 && e20 > e50)
        {
            double distE20 = (price - e20) / a;
            if (distE20 > -0.5 && distE20 < 1.0)
            {
                if (!has) setup = "상승추세 눌림 지지";
                has = true; bonus += 6; reasons.Add("상승추세 EMA 지지권(과확장 아님)");
            }
        }

        // 4) 거래량 마름→증가 (축적 후 관심 유입)
        double avg20 = 0; int c = 0; for (int i = Math.Max(0, last - 20); i < last; i++) { avg20 += vol[i]; c++; }
        avg20 = c > 0 ? avg20 / c : 0;
        if (avg20 > 0 && vol[last] > avg20 * 1.3 && bonus > 0)
        { bonus += 4; reasons.Add("거래량 유입 시작"); }

        return (setup, has, Math.Min(bonus, 28));
    }

    private static PredictResult Fallback(string sym, string itv, double price, DateTime t,
        string setup, bool hasSetup, double bonus, List<string> reasons, double atr)
    {
        if (double.IsNaN(atr) || atr <= 0) atr = price * 0.002;
        double score = Math.Clamp(bonus, 0, 60);
        if (reasons.Count == 0) reasons.Add("과거 표본 부족 — 셋업 기준 약식 판정");
        return new PredictResult
        {
            Symbol = sym, Interval = itv, Price = price, LastTime = t,
            ExpectedRisePct = bonus * 0.2, ExpectedNetPct = 0, ExpectedDrawPct = 0,
            WinRate = 0, Samples = 0, Setup = setup, HasSetup = hasSetup,
            Entry = price, Target = price + atr * 2, Stop = price - atr * 1.2,
            Score = score, Reasons = reasons
        };
    }
}

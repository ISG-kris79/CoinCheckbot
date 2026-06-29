namespace TradingCheckBot.Indicators;

/// <summary>
/// 보조지표 계산 모음. 모든 메서드는 입력 길이와 같은 길이의 배열을 반환하며,
/// 계산이 불가능한 앞부분 구간은 double.NaN 으로 채운다.
/// </summary>
public static class Ind
{
    /// <summary>단순이동평균(SMA)</summary>
    public static double[] Sma(IReadOnlyList<double> src, int period)
    {
        var outp = new double[src.Count];
        double sum = 0;
        for (int i = 0; i < src.Count; i++)
        {
            sum += src[i];
            if (i >= period) sum -= src[i - period];
            outp[i] = i >= period - 1 ? sum / period : double.NaN;
        }
        return outp;
    }

    /// <summary>지수이동평균(EMA)</summary>
    public static double[] Ema(IReadOnlyList<double> src, int period)
    {
        var outp = new double[src.Count];
        double k = 2.0 / (period + 1);
        double ema = 0;
        bool seeded = false;
        double seedSum = 0;
        for (int i = 0; i < src.Count; i++)
        {
            if (!seeded)
            {
                seedSum += src[i];
                if (i == period - 1)
                {
                    ema = seedSum / period; // 첫 EMA는 SMA로 시드
                    seeded = true;
                    outp[i] = ema;
                }
                else outp[i] = double.NaN;
            }
            else
            {
                ema = src[i] * k + ema * (1 - k);
                outp[i] = ema;
            }
        }
        return outp;
    }

    /// <summary>RSI (Wilder 평활)</summary>
    public static double[] Rsi(IReadOnlyList<double> close, int period = 14)
    {
        int n = close.Count;
        var outp = new double[n];
        for (int i = 0; i < n; i++) outp[i] = double.NaN;
        if (n <= period) return outp;

        double gain = 0, loss = 0;
        for (int i = 1; i <= period; i++)
        {
            double ch = close[i] - close[i - 1];
            if (ch >= 0) gain += ch; else loss -= ch;
        }
        double avgGain = gain / period;
        double avgLoss = loss / period;
        outp[period] = Rs(avgGain, avgLoss);

        for (int i = period + 1; i < n; i++)
        {
            double ch = close[i] - close[i - 1];
            double g = ch > 0 ? ch : 0;
            double l = ch < 0 ? -ch : 0;
            avgGain = (avgGain * (period - 1) + g) / period;
            avgLoss = (avgLoss * (period - 1) + l) / period;
            outp[i] = Rs(avgGain, avgLoss);
        }
        return outp;

        static double Rs(double ag, double al)
        {
            if (al == 0) return 100;
            double rs = ag / al;
            return 100 - 100 / (1 + rs);
        }
    }

    public sealed record MacdResult(double[] Macd, double[] Signal, double[] Hist);

    /// <summary>MACD (기본 12,26,9)</summary>
    public static MacdResult Macd(IReadOnlyList<double> close, int fast = 12, int slow = 26, int signal = 9)
    {
        var emaFast = Ema(close, fast);
        var emaSlow = Ema(close, slow);
        int n = close.Count;
        var macd = new double[n];
        for (int i = 0; i < n; i++)
            macd[i] = (double.IsNaN(emaFast[i]) || double.IsNaN(emaSlow[i])) ? double.NaN : emaFast[i] - emaSlow[i];

        // signal = macd 의 EMA. NaN 구간을 건너뛰고 유효 구간만 평활.
        var sig = new double[n];
        for (int i = 0; i < n; i++) sig[i] = double.NaN;
        int firstValid = Array.FindIndex(macd, v => !double.IsNaN(v));
        if (firstValid >= 0)
        {
            var slice = new List<double>();
            for (int i = firstValid; i < n; i++) slice.Add(macd[i]);
            var sigSlice = Ema(slice, signal);
            for (int i = 0; i < sigSlice.Length; i++) sig[firstValid + i] = sigSlice[i];
        }

        var hist = new double[n];
        for (int i = 0; i < n; i++)
            hist[i] = (double.IsNaN(macd[i]) || double.IsNaN(sig[i])) ? double.NaN : macd[i] - sig[i];

        return new MacdResult(macd, sig, hist);
    }

    public sealed record BollResult(double[] Upper, double[] Middle, double[] Lower);

    /// <summary>볼린저밴드 (기본 20, 2σ)</summary>
    public static BollResult Bollinger(IReadOnlyList<double> close, int period = 20, double mult = 2.0)
    {
        int n = close.Count;
        var mid = Sma(close, period);
        var up = new double[n];
        var lo = new double[n];
        for (int i = 0; i < n; i++)
        {
            if (i < period - 1) { up[i] = lo[i] = double.NaN; continue; }
            double mean = mid[i];
            double sumSq = 0;
            for (int j = i - period + 1; j <= i; j++)
            {
                double d = close[j] - mean;
                sumSq += d * d;
            }
            double sd = Math.Sqrt(sumSq / period);
            up[i] = mean + mult * sd;
            lo[i] = mean - mult * sd;
        }
        return new BollResult(up, mid, lo);
    }

    public sealed record StochResult(double[] K, double[] D);

    /// <summary>스토캐스틱 (기본 14,3,3)</summary>
    public static StochResult Stochastic(IReadOnlyList<double> high, IReadOnlyList<double> low, IReadOnlyList<double> close,
        int kPeriod = 14, int kSmooth = 3, int dPeriod = 3)
    {
        int n = close.Count;
        var rawK = new double[n];
        for (int i = 0; i < n; i++)
        {
            if (i < kPeriod - 1) { rawK[i] = double.NaN; continue; }
            double hh = double.MinValue, ll = double.MaxValue;
            for (int j = i - kPeriod + 1; j <= i; j++)
            {
                if (high[j] > hh) hh = high[j];
                if (low[j] < ll) ll = low[j];
            }
            double range = hh - ll;
            rawK[i] = range == 0 ? 50 : (close[i] - ll) / range * 100;
        }
        var k = SmaSkipNaN(rawK, kSmooth);
        var d = SmaSkipNaN(k, dPeriod);
        return new StochResult(k, d);
    }

    /// <summary>ADX (추세 강도, 기본 14)</summary>
    public static double[] Adx(IReadOnlyList<double> high, IReadOnlyList<double> low, IReadOnlyList<double> close, int period = 14)
    {
        int n = close.Count;
        var adx = new double[n];
        for (int i = 0; i < n; i++) adx[i] = double.NaN;
        if (n <= period * 2) return adx;

        var tr = new double[n];
        var plusDm = new double[n];
        var minusDm = new double[n];
        for (int i = 1; i < n; i++)
        {
            double upMove = high[i] - high[i - 1];
            double downMove = low[i - 1] - low[i];
            plusDm[i] = (upMove > downMove && upMove > 0) ? upMove : 0;
            minusDm[i] = (downMove > upMove && downMove > 0) ? downMove : 0;
            double hl = high[i] - low[i];
            double hc = Math.Abs(high[i] - close[i - 1]);
            double lc = Math.Abs(low[i] - close[i - 1]);
            tr[i] = Math.Max(hl, Math.Max(hc, lc));
        }

        // Wilder 평활 초기화
        double trS = 0, pS = 0, mS = 0;
        for (int i = 1; i <= period; i++) { trS += tr[i]; pS += plusDm[i]; mS += minusDm[i]; }

        var dx = new double[n];
        for (int i = 0; i < n; i++) dx[i] = double.NaN;

        for (int i = period + 1; i < n; i++)
        {
            trS = trS - trS / period + tr[i];
            pS = pS - pS / period + plusDm[i];
            mS = mS - mS / period + minusDm[i];
            double plusDi = trS == 0 ? 0 : 100 * pS / trS;
            double minusDi = trS == 0 ? 0 : 100 * mS / trS;
            double sum = plusDi + minusDi;
            dx[i] = sum == 0 ? 0 : 100 * Math.Abs(plusDi - minusDi) / sum;
        }

        // ADX = DX 의 Wilder 평균
        int start = period + 1;
        double adxVal = 0; int cnt = 0;
        for (int i = start; i < start + period && i < n; i++) { adxVal += dx[i]; cnt++; }
        if (cnt == 0) return adx;
        adxVal /= cnt;
        int adxStart = start + period - 1;
        if (adxStart < n) adx[adxStart] = adxVal;
        for (int i = adxStart + 1; i < n; i++)
        {
            adxVal = (adxVal * (period - 1) + dx[i]) / period;
            adx[i] = adxVal;
        }
        return adx;
    }

    /// <summary>ATR (평균 진폭, Wilder 평활, 기본 14). 손절/목표가 산정에 사용.</summary>
    public static double[] Atr(IReadOnlyList<double> high, IReadOnlyList<double> low, IReadOnlyList<double> close, int period = 14)
    {
        int n = close.Count;
        var outp = new double[n];
        for (int i = 0; i < n; i++) outp[i] = double.NaN;
        if (n <= period) return outp;

        var tr = new double[n];
        tr[0] = high[0] - low[0];
        for (int i = 1; i < n; i++)
        {
            double hl = high[i] - low[i];
            double hc = Math.Abs(high[i] - close[i - 1]);
            double lc = Math.Abs(low[i] - close[i - 1]);
            tr[i] = Math.Max(hl, Math.Max(hc, lc));
        }

        double sum = 0;
        for (int i = 1; i <= period; i++) sum += tr[i];
        double atr = sum / period;
        outp[period] = atr;
        for (int i = period + 1; i < n; i++)
        {
            atr = (atr * (period - 1) + tr[i]) / period;
            outp[i] = atr;
        }
        return outp;
    }

    // NaN 구간을 건너뛰고 이동평균을 적용하는 보조 함수
    private static double[] SmaSkipNaN(IReadOnlyList<double> src, int period)
    {
        int n = src.Count;
        var outp = new double[n];
        for (int i = 0; i < n; i++) outp[i] = double.NaN;
        var buf = new Queue<double>();
        double sum = 0;
        for (int i = 0; i < n; i++)
        {
            if (double.IsNaN(src[i])) continue;
            buf.Enqueue(src[i]);
            sum += src[i];
            if (buf.Count > period) sum -= buf.Dequeue();
            if (buf.Count == period) outp[i] = sum / period;
        }
        return outp;
    }
}

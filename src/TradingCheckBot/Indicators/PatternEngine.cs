using TradingCheckBot.Models;

namespace TradingCheckBot.Indicators;

/// <summary>스윙(고점/저점 변곡점) 1개</summary>
public readonly record struct Swing(int Index, double Price, bool IsHigh);

/// <summary>차트 패턴 1개의 판정 결과</summary>
public sealed class PatternResult
{
    public required string Name { get; init; }
    public required bool Detected { get; init; }
    public required Bias Direction { get; init; }
    public required string Detail { get; init; }
    /// <summary>신뢰도 0~1 (점수 가중에 사용)</summary>
    public required double Confidence { get; init; }
}

/// <summary>
/// 가격 차트 패턴 인식 엔진.
/// ZigZag 스윙을 기반으로 엘리엇 파동 · 피보나치 되돌림 · 삼각수렴 ·
/// 이중바닥(W)/이중천장(M) · V자 반등 · 헤드앤숄더(정/역) 등을 탐지한다.
/// 모든 판정은 보조 참고용 휴리스틱이며 100% 정확하지 않다.
/// </summary>
public static class PatternEngine
{
    /// <summary>
    /// 퍼센트 임계값 기반 ZigZag 스윙 추출.
    /// 직전 변곡점 대비 pct% 이상 반대로 움직일 때마다 새 변곡점을 확정한다.
    /// </summary>
    public static List<Swing> FindSwings(IReadOnlyList<double> high, IReadOnlyList<double> low, double pct = 0.02)
    {
        int n = high.Count;
        var swings = new List<Swing>();
        if (n < 3) return swings;

        // 첫 방향 결정: 초기 구간에서 고점/저점 중 먼저 도달
        int dir = 0; // 1=상승추적(고점 갱신), -1=하락추적(저점 갱신)
        double extreme = (high[0] + low[0]) / 2.0;
        int extremeIdx = 0;

        for (int i = 1; i < n; i++)
        {
            if (dir >= 0)
            {
                // 고점 추적
                if (high[i] > extreme) { extreme = high[i]; extremeIdx = i; }
                // 저점으로 반전?
                if (low[i] <= extreme * (1 - pct))
                {
                    swings.Add(new Swing(extremeIdx, extreme, true));
                    dir = -1; extreme = low[i]; extremeIdx = i;
                }
            }
            if (dir <= 0)
            {
                if (low[i] < extreme) { extreme = low[i]; extremeIdx = i; }
                if (high[i] >= extreme * (1 + pct))
                {
                    swings.Add(new Swing(extremeIdx, extreme, false));
                    dir = 1; extreme = high[i]; extremeIdx = i;
                }
            }
        }
        // 마지막 진행 중 극점 추가
        swings.Add(new Swing(extremeIdx, extreme, dir >= 0));
        // 중복/연속 동일 타입 정리
        return Clean(swings);
    }

    private static List<Swing> Clean(List<Swing> s)
    {
        var outp = new List<Swing>();
        foreach (var sw in s)
        {
            if (outp.Count > 0 && outp[^1].IsHigh == sw.IsHigh)
            {
                // 같은 타입 연속이면 더 극단값만 유지
                var prev = outp[^1];
                bool replace = sw.IsHigh ? sw.Price > prev.Price : sw.Price < prev.Price;
                if (replace) outp[^1] = sw;
            }
            else outp.Add(sw);
        }
        return outp;
    }

    /// <summary>모든 패턴을 탐지해 반환 (탐지된 것만 Detected=true).</summary>
    public static List<PatternResult> Detect(IReadOnlyList<Candle> candles)
    {
        var results = new List<PatternResult>();
        int n = candles.Count;
        if (n < 20) return results;

        var high = candles.Select(c => c.High).ToArray();
        var low = candles.Select(c => c.Low).ToArray();
        var close = candles.Select(c => c.Close).ToArray();
        double price = close[n - 1];

        // 변동성에 맞춘 임계값 (ATR 대략치 → %)
        double atr = Avg(TrueRanges(high, low, close), 14);
        double pct = Math.Clamp(atr / price * 1.5, 0.008, 0.05);
        var sw = FindSwings(high, low, pct);

        results.Add(DoubleBottom(sw, price, atr));
        results.Add(DoubleTop(sw, price, atr));
        results.Add(InverseHeadShoulders(sw, price, atr));
        results.Add(HeadShoulders(sw, price, atr));
        results.Add(Triangle(sw, price));
        results.Add(VBounce(candles, atr));
        results.Add(Fibonacci(sw, price));
        results.Add(ElliottWave(sw));

        return results;
    }

    // ───────────────────────── 이중바닥 (W) ─────────────────────────
    private static PatternResult DoubleBottom(List<Swing> sw, double price, double atr)
    {
        const string name = "이중바닥(W)";
        if (sw.Count >= 3)
        {
            // 마지막 3~4개 스윙에서 저-고-저 구조 탐색
            for (int i = sw.Count - 1; i >= 2; i--)
            {
                var low2 = sw[i]; var mid = sw[i - 1]; var low1 = sw[i - 2];
                if (!low2.IsHigh && mid.IsHigh && !low1.IsHigh)
                {
                    double diff = Math.Abs(low2.Price - low1.Price);
                    bool similarLows = diff < atr * 1.5;            // 두 저점 유사
                    bool neckline = mid.Price > low1.Price + atr;    // 사이 반등 충분
                    bool breaking = price >= mid.Price * 0.998;      // 넥라인 돌파 임박/돌파
                    if (similarLows && neckline)
                    {
                        double conf = breaking ? 0.8 : 0.5;
                        return Hit(name, Bias.Bull, conf,
                            breaking ? "두 저점 후 넥라인 돌파 — 상승 반전" : "두 저점 형성 — 넥라인 돌파 시 상승");
                    }
                }
            }
        }
        return Miss(name);
    }

    // ───────────────────────── 이중천장 (M) ─────────────────────────
    private static PatternResult DoubleTop(List<Swing> sw, double price, double atr)
    {
        const string name = "이중천장(M)";
        if (sw.Count >= 3)
        {
            for (int i = sw.Count - 1; i >= 2; i--)
            {
                var high2 = sw[i]; var mid = sw[i - 1]; var high1 = sw[i - 2];
                if (high2.IsHigh && !mid.IsHigh && high1.IsHigh)
                {
                    bool similar = Math.Abs(high2.Price - high1.Price) < atr * 1.5;
                    bool valley = mid.Price < high1.Price - atr;
                    bool breaking = price <= mid.Price * 1.002;
                    if (similar && valley)
                    {
                        double conf = breaking ? 0.8 : 0.5;
                        return Hit(name, Bias.Bear, conf,
                            breaking ? "두 고점 후 넥라인 이탈 — 하락 반전" : "두 고점 형성 — 넥라인 이탈 시 하락");
                    }
                }
            }
        }
        return Miss(name);
    }

    // ───────────────────────── 역헤드앤숄더 (상승 반전) ─────────────────────────
    private static PatternResult InverseHeadShoulders(List<Swing> sw, double price, double atr)
    {
        const string name = "역헤드앤숄더";
        // 저-고-저(머리)-고-저 : LS, H1, Head, H2, RS  (저점 5개 중 가운데가 최저)
        if (sw.Count >= 5)
        {
            for (int i = sw.Count - 1; i >= 4; i--)
            {
                var rs = sw[i - 0]; var h2 = sw[i - 1]; var head = sw[i - 2]; var h1 = sw[i - 3]; var ls = sw[i - 4];
                if (!rs.IsHigh && h2.IsHigh && !head.IsHigh && h1.IsHigh && !ls.IsHigh)
                {
                    bool headLowest = head.Price < ls.Price - atr * 0.3 && head.Price < rs.Price - atr * 0.3;
                    bool shouldersSimilar = Math.Abs(ls.Price - rs.Price) < atr * 2.0;
                    double neck = Math.Max(h1.Price, h2.Price);
                    bool breaking = price >= neck * 0.997;
                    if (headLowest && shouldersSimilar)
                        return Hit(name, Bias.Bull, breaking ? 0.85 : 0.55,
                            breaking ? "넥라인 돌파 — 강한 상승 반전" : "역H&S 형성 — 넥라인 돌파 대기");
                }
            }
        }
        return Miss(name);
    }

    // ───────────────────────── 헤드앤숄더 (하락 반전) ─────────────────────────
    private static PatternResult HeadShoulders(List<Swing> sw, double price, double atr)
    {
        const string name = "헤드앤숄더";
        if (sw.Count >= 5)
        {
            for (int i = sw.Count - 1; i >= 4; i--)
            {
                var rs = sw[i - 0]; var l2 = sw[i - 1]; var head = sw[i - 2]; var l1 = sw[i - 3]; var ls = sw[i - 4];
                if (rs.IsHigh && !l2.IsHigh && head.IsHigh && !l1.IsHigh && ls.IsHigh)
                {
                    bool headHighest = head.Price > ls.Price + atr * 0.3 && head.Price > rs.Price + atr * 0.3;
                    bool shouldersSimilar = Math.Abs(ls.Price - rs.Price) < atr * 2.0;
                    double neck = Math.Min(l1.Price, l2.Price);
                    bool breaking = price <= neck * 1.003;
                    if (headHighest && shouldersSimilar)
                        return Hit(name, Bias.Bear, breaking ? 0.85 : 0.55,
                            breaking ? "넥라인 이탈 — 강한 하락 반전" : "H&S 형성 — 넥라인 이탈 대기");
                }
            }
        }
        return Miss(name);
    }

    // ───────────────────────── 삼각수렴 ─────────────────────────
    private static PatternResult Triangle(List<Swing> sw, double price)
    {
        const string name = "삼각수렴";
        if (sw.Count >= 4)
        {
            // 최근 고점들과 저점들의 추세
            var highs = sw.Where(s => s.IsHigh).TakeLast(3).ToList();
            var lows = sw.Where(s => !s.IsHigh).TakeLast(3).ToList();
            if (highs.Count >= 2 && lows.Count >= 2)
            {
                double highSlope = highs[^1].Price - highs[0].Price;
                double lowSlope = lows[^1].Price - lows[0].Price;
                double hTol = highs[0].Price * 0.004;
                double lTol = lows[0].Price * 0.004;

                bool flatHighs = Math.Abs(highSlope) < hTol;
                bool risingLows = lowSlope > lTol;
                bool fallingHighs = highSlope < -hTol;
                bool flatLows = Math.Abs(lowSlope) < lTol;

                if (flatHighs && risingLows)
                    return Hit(name, Bias.Bull, 0.6, "상승삼각형 — 저점 상승, 상단 저항 돌파 시 상승");
                if (fallingHighs && flatLows)
                    return Hit(name, Bias.Bear, 0.6, "하락삼각형 — 고점 하락, 하단 이탈 시 하락");
                if (fallingHighs && risingLows)
                    return Hit(name, Bias.Neutral, 0.4, "대칭삼각형 — 변동성 수축, 돌파 방향 대기");
            }
        }
        return Miss(name);
    }

    // ───────────────────────── V자 반등 ─────────────────────────
    private static PatternResult VBounce(IReadOnlyList<Candle> c, double atr)
    {
        const string name = "V자 반등";
        int n = c.Count;
        if (n >= 10)
        {
            int k = 5;
            double dropStart = c[n - 1 - 2 * k].High;
            double bottom = c[n - 1 - k].Low;
            for (int i = n - 1 - 2 * k; i <= n - 1 - k; i++) bottom = Math.Min(bottom, c[i].Low);
            double now = c[n - 1].Close;

            double drop = dropStart - bottom;
            double rebound = now - bottom;
            bool sharpDrop = drop > atr * 3;
            bool sharpRebound = rebound > drop * 0.5;
            bool bullishNow = c[n - 1].Close > c[n - 1].Open && c[n - 2].Close > c[n - 2].Open;
            if (sharpDrop && sharpRebound && bullishNow)
                return Hit(name, Bias.Bull, 0.65, "급락 후 급반등 — V자 회복 진행");
        }
        return Miss(name);
    }

    // ───────────────────────── 피보나치 되돌림 ─────────────────────────
    private static PatternResult Fibonacci(List<Swing> sw, double price)
    {
        const string name = "피보나치 되돌림";
        if (sw.Count >= 2)
        {
            var last = sw[^1]; var prev = sw[^2];
            double a = prev.Price, b = last.Price;
            if (prev.IsHigh == false && last.IsHigh) // 상승 임펄스(저→고): 되돌림 지지 매수 노림
            {
                double range = b - a;
                if (range > 0)
                {
                    double r382 = b - range * 0.382;
                    double r618 = b - range * 0.618;
                    if (price <= r382 && price >= r618 * 0.995)
                        return Hit(name, Bias.Bull, 0.6, $"상승파동 0.382~0.618 되돌림 지지대({Fmt(r618)}~{Fmt(r382)}) — 반등 매수 구간");
                }
            }
            else if (prev.IsHigh && last.IsHigh == false) // 하락 임펄스(고→저): 되돌림 저항
            {
                double range = a - b;
                if (range > 0)
                {
                    double r382 = b + range * 0.382;
                    double r618 = b + range * 0.618;
                    if (price >= r382 && price <= r618 * 1.005)
                        return Hit(name, Bias.Bear, 0.55, $"하락파동 0.382~0.618 되돌림 저항대({Fmt(r382)}~{Fmt(r618)}) — 반락 주의");
                }
            }
        }
        return Miss(name);
    }

    // ───────────────────────── 엘리엇 파동 (단순 휴리스틱) ─────────────────────────
    private static PatternResult ElliottWave(List<Swing> sw)
    {
        const string name = "엘리엇 파동";
        // 최근 6개 변곡점으로 5파 임펄스 근사 판정
        if (sw.Count >= 6)
        {
            var s = sw.TakeLast(6).ToList();
            // 상승 임펄스: 저-고-저-고-저-고 이며 파동1<파동3 고점, 저점 상승
            if (!s[0].IsHigh && s[1].IsHigh && !s[2].IsHigh && s[3].IsHigh && !s[4].IsHigh && s[5].IsHigh)
            {
                bool higherHighs = s[3].Price > s[1].Price && s[5].Price > s[3].Price;
                bool higherLows = s[2].Price > s[0].Price && s[4].Price > s[2].Price;
                if (higherHighs && higherLows)
                    return Hit(name, Bias.Bull, 0.55, "상승 5파 임펄스 진행 — 추세 상승(5파 종료 후 조정 유의)");
            }
            // 하락 임펄스
            if (s[0].IsHigh && !s[1].IsHigh && s[2].IsHigh && !s[3].IsHigh && s[4].IsHigh && !s[5].IsHigh)
            {
                bool lowerLows = s[3].Price < s[1].Price && s[5].Price < s[3].Price;
                bool lowerHighs = s[2].Price < s[0].Price && s[4].Price < s[2].Price;
                if (lowerLows && lowerHighs)
                    return Hit(name, Bias.Bear, 0.55, "하락 5파 진행 — 추세 하락(반등은 조정일 가능성)");
            }
        }
        return Miss(name);
    }

    // ───────────────────────── 보조 ─────────────────────────
    private static PatternResult Hit(string name, Bias dir, double conf, string detail) =>
        new() { Name = name, Detected = true, Direction = dir, Confidence = conf, Detail = detail };

    private static PatternResult Miss(string name) =>
        new() { Name = name, Detected = false, Direction = Bias.Neutral, Confidence = 0, Detail = "미형성" };

    private static double[] TrueRanges(double[] h, double[] l, double[] c)
    {
        int n = c.Length;
        var tr = new double[n];
        tr[0] = h[0] - l[0];
        for (int i = 1; i < n; i++)
            tr[i] = Math.Max(h[i] - l[i], Math.Max(Math.Abs(h[i] - c[i - 1]), Math.Abs(l[i] - c[i - 1])));
        return tr;
    }

    private static double Avg(double[] v, int period)
    {
        int start = Math.Max(0, v.Length - period);
        double sum = 0; int cnt = 0;
        for (int i = start; i < v.Length; i++) { sum += v[i]; cnt++; }
        return cnt == 0 ? 0 : sum / cnt;
    }

    private static string Fmt(double v) => v >= 100 ? v.ToString("N1") : v.ToString("0.######");
}

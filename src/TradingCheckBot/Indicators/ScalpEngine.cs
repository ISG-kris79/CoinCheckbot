using TradingCheckBot.Models;

namespace TradingCheckBot.Indicators;

/// <summary>단타 진입 판정</summary>
public enum ScalpDecision { Enter, Wait, Avoid }

/// <summary>한 심볼의 단타 진입 판정 결과</summary>
public sealed class ScalpResult
{
    public required string Symbol { get; init; }
    public required string Interval { get; init; }
    public required double Price { get; init; }
    public required DateTime LastTime { get; init; }

    public required ScalpDecision Decision { get; init; }
    /// <summary>진입 방향 (진입일 때 의미)</summary>
    public TradeSide Side { get; init; } = TradeSide.Long;
    /// <summary>진입 후보 정렬용 품질 0~100 (진입일 때만 높음)</summary>
    public required int Quality { get; init; }
    /// <summary>발동한 진입 트리거 (없으면 대기/회피 사유)</summary>
    public required string Trigger { get; init; }
    public required List<string> Reasons { get; init; }

    public required double Atr { get; init; }
    public required double Entry { get; init; }
    public required double Target { get; init; }
    public required double Stop { get; init; }

    public double RiskReward
    {
        get
        {
            double risk = Entry - Stop, reward = Target - Entry;
            return risk <= 0 ? 0 : reward / risk;
        }
    }

    public string DecisionText => Decision switch
    {
        ScalpDecision.Enter => Side == TradeSide.Long ? "롱 진입" : "숏 진입",
        ScalpDecision.Wait => "대기",
        _ => "회피"
    };
}

/// <summary>
/// 단타 진입 판정 엔진 (트리거 기반).
/// "얼마나 강세냐"를 점수로 매겨 진입하는 게 아니라,
/// 구체적 진입 트리거(눌림목 반등 확인·과매도 반등·돌파 재테스트·지지 반등)가 발동하고
/// 손익비·안전 조건이 맞을 때만 '진입'을 낸다.
/// 강세 추세여도 트리거가 없으면 '대기', 하락 진행/칼받기/반전 증거면 '회피'.
/// </summary>
public static class ScalpEngine
{
    public static ScalpResult Evaluate(string symbol, string interval, IReadOnlyList<Candle> candles)
    {
        // 진행 중(미완성) 봉 제외 → 확정봉으로만 판정 (실시간 깜빡임 방지, 같은 봉 동안 신호 유지)
        if (candles.Count > 80) candles = candles.Take(candles.Count - 1).ToList();

        var close = candles.Select(c => c.Close).ToArray();
        var high = candles.Select(c => c.High).ToArray();
        var low = candles.Select(c => c.Low).ToArray();
        var vol = candles.Select(c => c.Volume).ToArray();
        int last = candles.Count - 1;
        double price = close[last];
        var c0 = candles[last];
        double rng0 = c0.High - c0.Low;
        bool bull0 = c0.Close > c0.Open;

        var ema9 = Ind.Ema(close, 9);
        var ema21 = Ind.Ema(close, 21);
        var ema50 = Ind.Ema(close, 50);
        var rsi = Ind.Rsi(close, 14);
        var stoch = Ind.Stochastic(high, low, close);
        var adxArr = Ind.Adx(high, low, close);
        var atrArr = Ind.Atr(high, low, close);
        double atr = Get(atrArr, last);
        if (double.IsNaN(atr) || atr <= 0) atr = Math.Max(price * 0.002, 1e-9);

        var ema20 = Ind.Ema(close, 20);
        double e9 = Get(ema9, last), e21 = Get(ema21, last), e50 = Get(ema50, last), e20 = Get(ema20, last);
        double e50Prev = Get(ema50, last - 3);

        // EMA20 위 여부 / EMA20 이격(ATR) — "20일선 위에서만 진입" 원칙
        bool aboveEma20 = !double.IsNaN(e20) && price > e20;
        double extAbove = (!double.IsNaN(e20) && atr > 0) ? (price - e20) / atr : 0;
        bool notExtended = extAbove < 2.5;   // EMA20에서 2.5 ATR 넘게 떨어져 있으면 추격 구간

        // EMA20×EMA50 골든크로스가 최근 발생했는가 (상승 전환 초입)
        int gcBar = -1;
        for (int i = last; i >= Math.Max(1, last - 20); i--)
        {
            double a = Get(ema20, i), b = Get(ema50, i), ap = Get(ema20, i - 1), bp = Get(ema50, i - 1);
            if (!double.IsNaN(a) && !double.IsNaN(ap) && a > b && ap <= bp) { gcBar = i; break; }
        }
        bool recentGoldenCross = gcBar >= 0 && (last - gcBar) <= 18 && !double.IsNaN(e20) && e20 > e50;
        double r = Get(rsi, last), rPrev = Get(rsi, last - 1);
        double k = Get(stoch.K, last), d = Get(stoch.D, last), kPrev = Get(stoch.K, last - 1), dPrev = Get(stoch.D, last - 1);
        double adx = Get(adxArr, last);

        // ── 1) 큰 흐름(맥락) ────────────────────────────────────────
        bool ctxUp = !double.IsNaN(e50) && e9 > e21 && price > e50;
        bool ctxDown = !double.IsNaN(e50) && e9 < e21 && e21 < e50 && price < e50;
        bool e50Rising = !double.IsNaN(e50Prev) && e50 > e50Prev;

        var reasons = new List<string>();
        if (ctxUp) reasons.Add(e50Rising ? "상승추세(EMA정배열·50상승)" : "상승추세(EMA정배열)");

        // ── 하락추세면 숏 경로로 판정 (롱은 회피, 숏 트리거 평가) ──
        if (ctxDown)
            return EvaluateShort(symbol, interval, candles, last, price, c0, rng0, bull0,
                ema20, ema50, rsi, stoch, atr, e20, e50);

        // ── 2) 실격 사유(회피) ──────────────────────────────────────
        // (a) 칼받기: 현재 강한 음봉 / 직전 저가 이탈 / 급락 진행
        double bearBody = (!bull0 && rng0 > 0) ? (c0.Open - c0.Close) / rng0 : 0;
        bool knife = (!bull0 && bearBody >= 0.55)
                     || (last >= 1 && c0.Close < candles[last - 1].Low);

        // (b) 고점 소진(반전) 증거: 약세 다이버전스 / 윗꼬리 거부 / 과매수 꺾임
        double divg = 0;
        int recPk = ArgMaxHigh(high, last - 4, last), prePk = ArgMaxHigh(high, last - 18, last - 6);
        if (recPk >= 0 && prePk >= 0 && high[recPk] > high[prePk])
        {
            double rA = Get(rsi, recPk), rB = Get(rsi, prePk);
            if (!double.IsNaN(rA) && !double.IsNaN(rB) && rA < rB - 2) divg = Math.Clamp((rB - rA) / 12.0, 0, 1);
        }
        double upper0 = c0.High - Math.Max(c0.Open, c0.Close);
        double wick = rng0 > 0 ? Math.Clamp((upper0 / rng0 - 0.45) / 0.4, 0, 1) : 0;
        double roll = (!double.IsNaN(r) && !double.IsNaN(rPrev) && rPrev >= 72 && r < rPrev) ? 1 : 0;
        bool exhaustion = (divg >= 0.4 && (wick >= 0.3 || roll > 0)) || (wick >= 0.6 && r >= 65);

        // ── 3) 진입 트리거 (상승/중립 맥락에서만) ───────────────────
        bool ctxOk = ctxUp || (!ctxDown); // 명확한 하락만 아니면 후보
        string trigger = "";
        double trigStrength = 0; // 0~1

        // 직전 저점/지지
        double swingLow = MinLow(low, last - 5, last);
        double rangeLo = MinLow(low, last - 20, last);
        double rangeHi = MaxHigh(high, last - 20, last - 2);

        if (ctxOk && !knife)
        {
            // (T0) 골든크로스 초입 진입: EMA20>EMA50 전환 후, 음봉 눌림 뒤 양봉이 EMA20 위로 마감 (과확장 전)
            bool recentDip = false;
            for (int i = last - 1; i >= Math.Max(0, last - 4); i--) if (candles[i].Close < candles[i].Open) recentDip = true;
            if (recentGoldenCross && aboveEma20 && notExtended && bull0 && c0.Close > e20 && recentDip && r < 68)
            {
                trigger = "골든크로스 초입 눌림 진입(EMA20)";
                trigStrength = 0.95;
                reasons.Add($"EMA20>50 골든크로스 {last - gcBar}봉 전 + 눌림 후 양봉(EMA20 위)");
            }

            // (T1) 눌림목 반등: 최근 EMA21까지 눌렸다가 현재 양봉이 EMA21 위로 마감
            bool dippedToEma = !double.IsNaN(e21) && MinLow(low, last - 3, last) <= e21 * 1.004;
            if (trigger.Length == 0 && ctxUp && dippedToEma && bull0 && c0.Close > e21)
            {
                trigger = "눌림목 반등(EMA21 지지)";
                trigStrength = 0.9;
                reasons.Add("EMA21 지지 후 양봉 반등");
            }
            // (T2) 과매도 반등: RSI 과매도에서 상승 전환 + 양봉, 또는 스토캐스틱 과매도 상향교차
            if (trigger.Length == 0)
            {
                bool rsiTurn = !double.IsNaN(r) && !double.IsNaN(rPrev) && rPrev <= 38 && r > rPrev && bull0;
                bool stochTurn = !double.IsNaN(k) && !double.IsNaN(d) && kPrev <= dPrev && k > d && k < 35;
                if (rsiTurn || stochTurn)
                {
                    trigger = "과매도 반등";
                    trigStrength = 0.8;
                    if (rsiTurn) reasons.Add($"RSI 과매도 반등 {rPrev:F0}→{r:F0}");
                    if (stochTurn) reasons.Add($"스토캐스틱 과매도 상향교차");
                }
            }
            // (T3) 돌파 후 재테스트: 직전 저항 위로 올라온 뒤 그 부근을 지지로 양봉
            if (trigger.Length == 0 && !double.IsNaN(rangeHi))
            {
                bool retest = price >= rangeHi * 0.999 && MinLow(low, last - 2, last) <= rangeHi * 1.004 && bull0;
                if (ctxUp && retest)
                {
                    trigger = "돌파 후 지지 재테스트";
                    trigStrength = 0.75;
                    reasons.Add("저항 돌파 후 지지 확인");
                }
            }
            // (T4) 지지 반등: 최근 박스 저점 부근에서 반등 양봉
            if (trigger.Length == 0)
            {
                bool nearSupport = c0.Low <= rangeLo * 1.01;
                bool bounce = bull0 && c0.Close > (c0.High + c0.Low) / 2;
                if (nearSupport && bounce)
                {
                    trigger = "지지선 반등";
                    trigStrength = 0.6;
                    reasons.Add("박스 저점 지지 반등");
                }
            }
        }

        // 거래량 확인(보조)
        double avgVol = AvgVolume(vol, last, 20);
        bool volOk = avgVol > 0 && vol[last] >= avgVol * 1.2;
        if (volOk) reasons.Add($"거래량 증가({vol[last] / avgVol * 100:F0}%)");

        // 차트 패턴(보조 확인)
        var bullPatterns = PatternEngine.Detect(candles)
            .Where(p => p.Detected && p.Direction == Bias.Bull).Select(p => p.Name).ToList();
        foreach (var pn in bullPatterns) reasons.Add($"📐{pn}");

        // ── 4) 손익비/안전 ──────────────────────────────────────────
        double stop = Math.Min(swingLow, price - atr * 0.8) - atr * 0.1;
        double risk = price - stop;
        double target = price + Math.Max(risk * 1.8, atr * 1.5);
        double rr = risk > 0 ? (target - price) / risk : 0;
        bool rrOk = rr >= 1.3 && risk > atr * 0.15 && risk <= atr * 2.5; // 손절이 너무 멀면(과확장) 탈락

        // ── 5) 최종 판정 ────────────────────────────────────────────
        ScalpDecision decision;
        string note;
        bool overbought = !double.IsNaN(r) && r >= 72;
        if (ctxDown) { decision = ScalpDecision.Avoid; note = "하락추세 — 롱 회피"; }
        else if (knife) { decision = ScalpDecision.Avoid; note = "급락/저가이탈 진행 — 칼받기 회피"; }
        else if (exhaustion) { decision = ScalpDecision.Avoid; note = "고점 반전 증거(다이버전스/거부) — 회피"; }
        else if (!aboveEma20) { decision = ScalpDecision.Wait; note = "EMA20 아래 — 20일선 회복 대기"; }
        else if (!notExtended || overbought) { decision = ScalpDecision.Wait; note = $"EMA20 +{extAbove:F1}ATR 과확장/과매수 — 추격 금지, 눌림 대기"; }
        else if (trigger.Length > 0 && rrOk) { decision = ScalpDecision.Enter; note = trigger; }
        else if (trigger.Length > 0) { decision = ScalpDecision.Wait; note = "트리거 있으나 손익비/거리 부적합 — 대기"; }
        else { decision = ScalpDecision.Wait; note = ctxUp ? "상승추세지만 진입 트리거 없음 — 눌림 대기" : "방향 불명확 — 대기"; }

        // ── 6) 품질 점수 (진입 후보 정렬용) ─────────────────────────
        int quality;
        if (decision == ScalpDecision.Enter)
        {
            double q = 55 + trigStrength * 25;          // 트리거 기본 55~80
            if (ctxUp) q += 6;
            if (e50Rising) q += 3;
            if (volOk) q += 6;
            if (bullPatterns.Count > 0) q += 5;
            if (!double.IsNaN(adx) && adx >= 20) q += 3;
            q += Math.Min(6, (rr - 1.3) * 4);           // 손익비 보너스
            quality = (int)Math.Clamp(Math.Round(q), 0, 100);
        }
        else if (decision == ScalpDecision.Wait)
        {
            double q = 30 + (ctxUp ? 12 : 0) + (e50Rising ? 4 : 0);
            quality = (int)Math.Clamp(Math.Round(q), 0, 55);
        }
        else quality = Math.Max(0, 18 - (knife ? 10 : 0));

        if (reasons.Count == 0) reasons.Add(note);

        // 진입 계획가: 진입이면 현재가, 대기면 눌림 목표(EMA20)에서 매수 대기
        double planEntry = decision == ScalpDecision.Enter ? price
                          : (!double.IsNaN(e20) && aboveEma20 ? e20 : price);
        double planStop = Math.Min(swingLow, planEntry - atr * 0.8) - atr * 0.1;
        double planRisk = planEntry - planStop;
        double planTarget = planEntry + Math.Max(planRisk * 1.8, atr * 1.5);

        return new ScalpResult
        {
            Symbol = symbol,
            Interval = interval,
            Price = price,
            LastTime = c0.OpenTime,
            Decision = decision,
            Quality = quality,
            Trigger = note,
            Reasons = reasons,
            Atr = atr,
            Entry = planEntry,
            Target = planTarget,
            Stop = planStop
        };
    }

    // ───────────────────────── 숏(하락) 경로 ─────────────────────────
    private static ScalpResult EvaluateShort(string symbol, string interval, IReadOnlyList<Candle> candles,
        int last, double price, Candle c0, double rng0, bool bull0,
        double[] ema20, double[] ema50, double[] rsi, Ind.StochResult stoch, double atr, double e20, double e50)
    {
        var close = candles.Select(c => c.Close).ToArray();
        var high = candles.Select(c => c.High).ToArray();
        var low = candles.Select(c => c.Low).ToArray();
        var vol = candles.Select(c => c.Volume).ToArray();
        double r = Get(rsi, last), rPrev = Get(rsi, last - 1);
        double k = Get(stoch.K, last), d = Get(stoch.D, last), kPrev = Get(stoch.K, last - 1), dPrev = Get(stoch.D, last - 1);

        var reasons = new List<string> { "하락추세(EMA 역배열)" };

        bool belowEma20 = !double.IsNaN(e20) && price < e20;
        double extBelow = (!double.IsNaN(e20) && atr > 0) ? (e20 - price) / atr : 0;
        bool notExtended = extBelow < 2.5;          // EMA20 아래로 2.5 ATR 넘으면 추격 구간
        bool oversold = !double.IsNaN(r) && r <= 28;

        // EMA20×EMA50 데드크로스 최근?
        int dcBar = -1;
        for (int i = last; i >= Math.Max(1, last - 20); i--)
        {
            double a = Get(ema20, i), b = Get(ema50, i), ap = Get(ema20, i - 1), bp = Get(ema50, i - 1);
            if (!double.IsNaN(a) && !double.IsNaN(ap) && a < b && ap >= bp) { dcBar = i; break; }
        }
        bool recentDeadCross = dcBar >= 0 && (last - dcBar) <= 18 && !double.IsNaN(e20) && e20 < e50;

        // 숏 스퀴즈 회피: 현재 강한 양봉 / 직전 고가 돌파(급등)
        double bullBody = (bull0 && rng0 > 0) ? (c0.Close - c0.Open) / rng0 : 0;
        bool squeeze = (bull0 && bullBody >= 0.55) || (last >= 1 && c0.Close > candles[last - 1].High);

        // 바닥 반등 증거(강세 다이버전스/아랫꼬리): 숏 회피
        double bdiv = 0;
        int recTr = ArgMinLow(low, last - 4, last), preTr = ArgMinLow(low, last - 18, last - 6);
        if (recTr >= 0 && preTr >= 0 && low[recTr] < low[preTr])
        {
            double rA = Get(rsi, recTr), rB = Get(rsi, preTr);
            if (!double.IsNaN(rA) && !double.IsNaN(rB) && rA > rB + 2) bdiv = Math.Clamp((rA - rB) / 12.0, 0, 1);
        }
        double lowerWick = rng0 > 0 ? Math.Clamp(((Math.Min(c0.Open, c0.Close) - c0.Low) / rng0 - 0.45) / 0.4, 0, 1) : 0;
        double rollUp = (!double.IsNaN(r) && !double.IsNaN(rPrev) && rPrev <= 28 && r > rPrev) ? 1 : 0;
        bool bottomReversal = (bdiv >= 0.4 && (lowerWick >= 0.3 || rollUp > 0)) || (lowerWick >= 0.6 && r <= 35);

        // ── 숏 트리거 ──
        string trigger = ""; double trigStrength = 0;
        double swingHigh = MaxHigh(high, last - 5, last);
        double rangeHi = MaxHigh(high, last - 20, last);
        double rangeLo = MinLow(low, last - 20, last - 2);

        if (belowEma20 && !squeeze)
        {
            // (S0) 데드크로스 초입: 되돌림(양봉) 후 현재 음봉이 EMA20 아래로 마감
            bool recentPop = false;
            for (int i = last - 1; i >= Math.Max(0, last - 4); i--) if (candles[i].Close > candles[i].Open) recentPop = true;
            if (recentDeadCross && notExtended && !bull0 && c0.Close < e20 && recentPop && r > 32)
            { trigger = "데드크로스 초입 반락 숏(EMA20)"; trigStrength = 0.95; reasons.Add($"EMA20<50 데드크로스 {last - dcBar}봉 전 + 되돌림 후 음봉(EMA20 아래)"); }

            // (S1) EMA20 저항 반락: 되돌려 EMA20 부근 닿았다가 음봉
            if (trigger.Length == 0 && !double.IsNaN(e20) && MaxHigh(high, last - 3, last) >= e20 * 0.996 && !bull0 && c0.Close < e20)
            { trigger = "EMA20 저항 반락 숏"; trigStrength = 0.85; reasons.Add("EMA20 저항 확인 후 음봉"); }

            // (S2) 과매수 반락: RSI 되돌림 고점에서 꺾임 + 음봉, 또는 스토캐스틱 하향교차
            if (trigger.Length == 0)
            {
                bool rsiTurn = !double.IsNaN(r) && !double.IsNaN(rPrev) && rPrev >= 58 && r < rPrev && !bull0;
                bool stochTurn = !double.IsNaN(k) && !double.IsNaN(d) && kPrev >= dPrev && k < d && k > 65;
                if (rsiTurn || stochTurn) { trigger = "과매수 반락 숏"; trigStrength = 0.8; reasons.Add("되돌림 과매수 반락"); }
            }

            // (S3) 붕괴 후 재테스트: 지지 붕괴 뒤 그 부근 되돌림에서 음봉
            if (trigger.Length == 0 && !double.IsNaN(rangeLo))
            {
                bool retest = price <= rangeLo * 1.001 && MaxHigh(high, last - 2, last) >= rangeLo * 0.996 && !bull0;
                if (retest) { trigger = "지지 붕괴 후 재테스트 숏"; trigStrength = 0.75; reasons.Add("지지 붕괴 후 저항 확인"); }
            }

            // (S4) 저항 반락: 박스 고점 부근 음봉
            if (trigger.Length == 0)
            {
                bool nearRes = c0.High >= rangeHi * 0.99;
                bool reject = !bull0 && c0.Close < (c0.High + c0.Low) / 2;
                if (nearRes && reject) { trigger = "저항선 반락 숏"; trigStrength = 0.6; reasons.Add("박스 고점 저항 반락"); }
            }
        }

        double avgVol = AvgVolume(vol, last, 20);
        bool volOk = avgVol > 0 && vol[last] >= avgVol * 1.2;
        if (volOk) reasons.Add($"거래량 증가({vol[last] / avgVol * 100:F0}%)");
        var bearPatterns = PatternEngine.Detect(candles).Where(p => p.Detected && p.Direction == Bias.Bear).Select(p => p.Name).ToList();
        foreach (var pn in bearPatterns) reasons.Add($"📐{pn}");

        // 손익비/안전 (숏: 손절 위, 목표 아래)
        double stop = Math.Max(swingHigh, price + atr * 0.8) + atr * 0.1;
        double risk = stop - price;
        double target = price - Math.Max(risk * 1.8, atr * 1.5);
        double rr = risk > 0 ? (price - target) / risk : 0;
        bool rrOk = rr >= 1.3 && risk > atr * 0.15 && risk <= atr * 2.5;

        ScalpDecision decision; string note;
        if (squeeze) { decision = ScalpDecision.Avoid; note = "급등/신고가 진행 — 숏 회피(스퀴즈 위험)"; }
        else if (bottomReversal) { decision = ScalpDecision.Avoid; note = "바닥 반등 증거(다이버전스/거부) — 숏 회피"; }
        else if (!belowEma20) { decision = ScalpDecision.Wait; note = "EMA20 위 — 숏은 20일선 아래에서"; }
        else if (!notExtended || oversold) { decision = ScalpDecision.Wait; note = $"EMA20 -{extBelow:F1}ATR 과확장/과매도 — 추격 금지, 반등 대기"; }
        else if (trigger.Length > 0 && rrOk) { decision = ScalpDecision.Enter; note = trigger; }
        else if (trigger.Length > 0) { decision = ScalpDecision.Wait; note = "숏 트리거 있으나 손익비/거리 부적합 — 대기"; }
        else { decision = ScalpDecision.Wait; note = "하락추세지만 숏 트리거 없음 — 반등 후 대기"; }

        int quality;
        if (decision == ScalpDecision.Enter)
        {
            double q = 55 + trigStrength * 25;
            if (volOk) q += 6;
            if (bearPatterns.Count > 0) q += 5;
            q += Math.Min(6, (rr - 1.3) * 4);
            quality = (int)Math.Clamp(Math.Round(q), 0, 100);
        }
        else if (decision == ScalpDecision.Wait) quality = 35;
        else quality = Math.Max(0, 18 - (squeeze ? 10 : 0));

        if (reasons.Count == 0) reasons.Add(note);

        // 진입 계획가: 진입이면 현재가, 대기면 되돌림 목표(EMA20)에서 매도 대기
        double planEntry = decision == ScalpDecision.Enter ? price
                          : (!double.IsNaN(e20) && belowEma20 ? e20 : price);
        double planStop = Math.Max(swingHigh, planEntry + atr * 0.8) + atr * 0.1;
        double planRisk = planStop - planEntry;
        double planTarget = planEntry - Math.Max(planRisk * 1.8, atr * 1.5);

        return new ScalpResult
        {
            Symbol = symbol, Interval = interval, Price = price, LastTime = c0.OpenTime,
            Decision = decision, Side = TradeSide.Short, Quality = quality, Trigger = note, Reasons = reasons,
            Atr = atr, Entry = planEntry, Target = planTarget, Stop = planStop
        };
    }

    // ───────────────────────── 보조 ─────────────────────────
    private static double Get(double[] arr, int i) => i >= 0 && i < arr.Length ? arr[i] : double.NaN;

    private static int ArgMinLow(double[] low, int from, int to)
    {
        from = Math.Max(0, from); to = Math.Min(low.Length - 1, to);
        if (from > to) return -1;
        int idx = from; double mn = low[from];
        for (int i = from + 1; i <= to; i++) if (low[i] < mn) { mn = low[i]; idx = i; }
        return idx;
    }

    private static double AvgVolume(double[] vol, int last, int period)
    {
        int start = Math.Max(0, last - period);
        int cnt = last - start; // 현재 봉 제외
        if (cnt <= 0) return 0;
        double sum = 0;
        for (int i = start; i < last; i++) sum += vol[i];
        return sum / cnt;
    }

    private static int ArgMaxHigh(double[] high, int from, int to)
    {
        from = Math.Max(0, from); to = Math.Min(high.Length - 1, to);
        if (from > to) return -1;
        int idx = from; double mx = high[from];
        for (int i = from + 1; i <= to; i++) if (high[i] > mx) { mx = high[i]; idx = i; }
        return idx;
    }

    private static double MaxHigh(double[] high, int from, int to)
    {
        from = Math.Max(0, from); to = Math.Min(high.Length - 1, to);
        if (from > to) return double.NaN;
        double mx = double.MinValue;
        for (int i = from; i <= to; i++) mx = Math.Max(mx, high[i]);
        return mx;
    }

    private static double MinLow(double[] low, int from, int to)
    {
        from = Math.Max(0, from); to = Math.Min(low.Length - 1, to);
        if (from > to) return double.NaN;
        double mn = double.MaxValue;
        for (int i = from; i <= to; i++) mn = Math.Min(mn, low[i]);
        return mn;
    }
}

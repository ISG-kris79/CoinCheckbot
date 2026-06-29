using TradingCheckBot.Models;

namespace TradingCheckBot.Indicators;

/// <summary>
/// 단타 진입 계획(plan)을 '상태'로 유지하는 관리자.
/// ScalpEngine은 매 호출마다 순간 셋업을 새로 계산하므로, 가격이 진입대기 자리에 오면
/// 조건이 바뀌어 신호가 사라진다. 그건 예측이 아니다.
/// PlanManager는 한번 발생한 계획(진입가/목표/손절을 그 시점에 고정)을 저장하고,
/// 실제 사건이 일어날 때만 상태를 전이시킨다:
///   진입대기 → (진입가 도달) 진입체결 → (목표 도달) 익절 / (손절 도달) 손절
///   진입대기 → (손절선 먼저 이탈 / 대기 만료) 무효
/// 이렇게 해야 사용자가 보고 실제로 진입할 수 있는 '예측'이 된다.
/// </summary>
public static class PlanManager
{
    private const int MaxWaitBars = 12;   // 진입가 미도달 시 만료까지 대기 봉 수
    private static readonly object _lock = new();
    private static readonly Dictionary<string, Plan> _book = new();

    private sealed class Plan
    {
        public required TradeSide Side;
        public required double Entry;
        public required double Target;
        public required double Stop;
        public required DateTime IssuedTime;   // 발생(확정봉) 시각 — 고정 기준
        public required bool ImmediateEntry;   // 발생 즉시 진입(Enter)이었는지
        public required int Quality;
        public required string Trigger;
        public required List<string> Reasons;
    }

    /// <summary>계획을 유지하며 현재 상태를 반영한 결과를 돌려준다.</summary>
    public static ScalpResult Evaluate(string symbol, string interval, IReadOnlyList<Candle> candles)
    {
        var s = ScalpEngine.Evaluate(symbol, interval, candles);
        int lastClosed = candles.Count - 2; // 마지막 확정봉
        if (lastClosed < 1) return s;
        string key = symbol + "|" + interval;

        lock (_lock)
        {
            if (_book.TryGetValue(key, out var p))
            {
                var view = Simulate(p, candles, lastClosed, s, out bool terminal);
                if (terminal) _book.Remove(key);
                return view;
            }

            var np = TryCreate(s, candles[lastClosed].OpenTime);
            if (np != null)
            {
                _book[key] = np;
                return Simulate(np, candles, lastClosed, s, out _);
            }
            return s; // 관망/회피 — 계획 없음
        }
    }

    private static Plan? TryCreate(ScalpResult s, DateTime issueTime)
    {
        if (s.Decision == ScalpDecision.Enter)
            return new Plan
            {
                Side = s.Side, Entry = s.Entry, Target = s.Target, Stop = s.Stop,
                IssuedTime = issueTime, ImmediateEntry = true, Quality = s.Quality,
                Trigger = s.Trigger, Reasons = s.Reasons
            };

        if (s.Decision == ScalpDecision.Wait)
        {
            // 현재가보다 유리한 진입대기 자리가 있을 때만 계획 생성 (롱=눌림, 숏=되돌림)
            bool concrete = (s.Side == TradeSide.Long && s.Entry < s.Price * 0.999)
                         || (s.Side == TradeSide.Short && s.Entry > s.Price * 1.001);
            if (concrete)
                return new Plan
                {
                    Side = s.Side, Entry = s.Entry, Target = s.Target, Stop = s.Stop,
                    IssuedTime = issueTime, ImmediateEntry = false, Quality = s.Quality,
                    Trigger = s.Trigger, Reasons = s.Reasons
                };
        }
        return null;
    }

    // 발생 시점부터 마지막 확정봉까지 재시뮬레이션 → 현재 상태 결정 (멱등)
    private static ScalpResult Simulate(Plan p, IReadOnlyList<Candle> candles, int lastClosed, ScalpResult s, out bool terminal)
    {
        int issueIdx = -1;
        for (int i = lastClosed; i >= Math.Max(0, lastClosed - 60); i--)
            if (candles[i].OpenTime == p.IssuedTime) { issueIdx = i; break; }
        if (issueIdx < 0) issueIdx = lastClosed;

        bool isLong = p.Side == TradeSide.Long;
        string state = p.ImmediateEntry ? "Entered" : "Waiting";
        int barsWaited = 0;

        for (int i = issueIdx + 1; i <= lastClosed; i++)
        {
            var c = candles[i];
            if (state == "Waiting")
            {
                barsWaited++;
                // 손절선이 진입 전에 먼저 깨지면 무효
                if (isLong ? c.Low <= p.Stop : c.High >= p.Stop) { state = "Invalid"; break; }
                // 진입가 도달 → 체결
                if (isLong ? c.Low <= p.Entry : c.High >= p.Entry) { state = "Entered"; continue; }
                if (barsWaited > MaxWaitBars) { state = "Expired"; break; }
            }
            else if (state == "Entered")
            {
                if (isLong ? c.High >= p.Target : c.Low <= p.Target) { state = "TargetHit"; break; }
                if (isLong ? c.Low <= p.Stop : c.High >= p.Stop) { state = "StoppedOut"; break; }
            }
        }
        if (state == "Waiting" && (lastClosed - issueIdx) > MaxWaitBars) state = "Expired";

        terminal = state is "TargetHit" or "StoppedOut" or "Invalid" or "Expired";

        ScalpDecision decision; string note; var reasons = new List<string>(p.Reasons);
        string sideTxt = isLong ? "롱" : "숏";
        switch (state)
        {
            case "Waiting":
                decision = ScalpDecision.Wait;
                note = $"{sideTxt} 진입대기 @ {Fmt(p.Entry)} (대기 {lastClosed - issueIdx}/{MaxWaitBars}봉)";
                break;
            case "Entered":
                decision = ScalpDecision.Enter;
                note = $"{sideTxt} 진입 체결 @ {Fmt(p.Entry)} — 목표/손절 유지";
                break;
            case "TargetHit":
                decision = ScalpDecision.Wait;
                note = $"🎯 목표 도달 — 익절 구간 ({Fmt(p.Target)})";
                reasons.Insert(0, "계획 완료: 목표 도달");
                break;
            case "StoppedOut":
                decision = ScalpDecision.Avoid;
                note = $"손절 도달 — 계획 종료 ({Fmt(p.Stop)})";
                reasons.Insert(0, "계획 종료: 손절");
                break;
            case "Invalid":
                decision = ScalpDecision.Avoid;
                note = "진입 전 손절선 이탈 — 무효";
                break;
            default: // Expired
                decision = ScalpDecision.Wait;
                note = "진입가 미도달 — 대기 만료(무효)";
                break;
        }

        return new ScalpResult
        {
            Symbol = s.Symbol, Interval = s.Interval, Price = s.Price, LastTime = s.LastTime,
            Decision = decision, Side = p.Side, Quality = p.Quality, Trigger = note, Reasons = reasons,
            Atr = s.Atr, Entry = p.Entry, Target = p.Target, Stop = p.Stop, Warning = s.Warning
        };
    }

    private static string Fmt(double v) => v >= 1000 ? v.ToString("N1") : v >= 1 ? v.ToString("N3") : v.ToString("0.######");
}

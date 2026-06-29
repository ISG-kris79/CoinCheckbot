namespace TradingCheckBot.Models;

/// <summary>
/// 하나의 캔들(봉) 데이터. 바이낸스 kline 한 건에 대응한다.
/// </summary>
public sealed record Candle(
    DateTime OpenTime,
    double Open,
    double High,
    double Low,
    double Close,
    double Volume);

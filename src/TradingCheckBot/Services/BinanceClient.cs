using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using TradingCheckBot.Models;

namespace TradingCheckBot.Services;

/// <summary>
/// 바이낸스 USDT-M 선물(futures) 공개 시세 API 클라이언트.
/// 인증/키가 필요 없는 공개 엔드포인트만 사용한다.
/// </summary>
public sealed class BinanceClient
{
    // USDT-M 선물 기본 도메인. 지역 차단 시 대체 도메인으로 자동 폴백한다.
    private static readonly string[] BaseUrls =
    {
        "https://fapi.binance.com",
        "https://fapi.binance.com", // 1차 재시도
    };

    private readonly HttpClient _http;

    public BinanceClient()
    {
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        _http.DefaultRequestHeaders.Add("User-Agent", "TradingCheckBot/1.0");
    }

    /// <summary>
    /// 지정한 심볼/인터벌의 캔들을 최신순이 아닌 과거→현재 순서로 반환한다.
    /// </summary>
    /// <param name="symbol">예: BTCUSDT</param>
    /// <param name="interval">바이낸스 인터벌 문자열 (1m,5m,15m,1h,4h,1d,1w,1M)</param>
    /// <param name="limit">최대 1500</param>
    public async Task<List<Candle>> GetKlinesAsync(string symbol, string interval, int limit = 500, CancellationToken ct = default)
    {
        symbol = symbol.Trim().ToUpperInvariant();
        var query = $"/fapi/v1/klines?symbol={symbol}&interval={interval}&limit={limit}";

        Exception? last = null;
        foreach (var baseUrl in BaseUrls)
        {
            try
            {
                using var resp = await _http.GetAsync(baseUrl + query, ct).ConfigureAwait(false);
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    throw new HttpRequestException($"HTTP {(int)resp.StatusCode}: {Truncate(body)}");

                return Parse(body);
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }
        throw new InvalidOperationException($"바이낸스 데이터 요청 실패: {last?.Message}", last);
    }

    /// <summary>24시간 티커 1건 (스캐너 표시·정렬용)</summary>
    public sealed record SymbolTicker(string Symbol, double LastPrice, double PriceChangePercent, double QuoteVolume);

    /// <summary>
    /// USDT 무기한 선물 심볼 중 거래대금(quoteVolume) 상위 N개를 반환한다.
    /// 유동성이 높은 종목만 단타 스캔 대상으로 추리기 위함.
    /// </summary>
    public async Task<List<SymbolTicker>> GetTopSymbolsAsync(int count = 30, CancellationToken ct = default)
    {
        const string query = "/fapi/v1/ticker/24hr";
        Exception? last = null;
        foreach (var baseUrl in BaseUrls)
        {
            try
            {
                using var resp = await _http.GetAsync(baseUrl + query, ct).ConfigureAwait(false);
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    throw new HttpRequestException($"HTTP {(int)resp.StatusCode}: {Truncate(body)}");

                using var doc = JsonDocument.Parse(body);
                var list = new List<SymbolTicker>();
                foreach (var t in doc.RootElement.EnumerateArray())
                {
                    string sym = t.GetProperty("symbol").GetString() ?? "";
                    // USDT 마진 무기한만 (BTCUSDT 형태). USDC·불꽃·인덱스 등은 제외.
                    if (!sym.EndsWith("USDT", StringComparison.Ordinal)) continue;
                    double price = Dp(t, "lastPrice");
                    double chg = Dp(t, "priceChangePercent");
                    double qv = Dp(t, "quoteVolume");
                    list.Add(new SymbolTicker(sym, price, chg, qv));
                }
                return list.OrderByDescending(x => x.QuoteVolume).Take(count).ToList();
            }
            catch (Exception ex) { last = ex; }
        }
        throw new InvalidOperationException($"심볼 목록 요청 실패: {last?.Message}", last);
    }

    private static double Dp(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var e) && double.TryParse(e.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static List<Candle> Parse(string json)
    {
        // 응답 형식: [[ openTime, "open", "high", "low", "close", "volume", closeTime, ... ], ...]
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var result = new List<Candle>(root.GetArrayLength());

        foreach (var k in root.EnumerateArray())
        {
            long openTimeMs = k[0].GetInt64();
            double open = D(k[1]);
            double high = D(k[2]);
            double low = D(k[3]);
            double close = D(k[4]);
            double vol = D(k[5]);
            var openTime = DateTimeOffset.FromUnixTimeMilliseconds(openTimeMs).LocalDateTime;
            result.Add(new Candle(openTime, open, high, low, close, vol));
        }
        return result;
    }

    private static double D(JsonElement e) =>
        double.Parse(e.GetString() ?? "0", CultureInfo.InvariantCulture);

    private static string Truncate(string s) => s.Length > 200 ? s[..200] : s;
}

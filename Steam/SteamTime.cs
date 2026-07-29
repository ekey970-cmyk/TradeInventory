using System.Text.Json;
using SteamKit2;
using SteamKit2.Internal;

namespace TradeInventory.Steam;

/// <summary>ASF GetSteamTime — prefer SteamKit2 TwoFactor.QueryTime, fallback HTTP.</summary>
public static class SteamTime
{
    static readonly SemaphoreSlim Gate = new(1, 1);
    static int? _diff;
    static DateTimeOffset _checkedAt;
    static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);

    public static async Task<long> NowAsync(SteamWebSession? session = null, HttpClient? http = null, CancellationToken ct = default)
    {
        if (_diff is int d && DateTimeOffset.UtcNow - _checkedAt < Ttl)
            return DateTimeOffset.UtcNow.ToUnixTimeSeconds() + d;

        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_diff is int d2 && DateTimeOffset.UtcNow - _checkedAt < Ttl)
                return DateTimeOffset.UtcNow.ToUnixTimeSeconds() + d2;

            long server = 0;
            if (session?.Client?.IsConnected == true)
            {
                try
                {
                    var unified = session.Client.GetHandler<SteamUnifiedMessages>();
                    if (unified is not null)
                    {
                        var svc = unified.CreateService<TwoFactor>();
                        var resp = await svc.QueryTime(new CTwoFactor_Time_Request()).ToTask().WaitAsync(ct)
                            .ConfigureAwait(false);
                        if (resp.Result == EResult.OK && resp.Body.server_time != 0)
                            server = (long)resp.Body.server_time;
                    }
                }
                catch { /* fall through */ }
            }

            if (server == 0)
            {
                try
                {
                    http ??= new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                    using var resp = await http.PostAsync(
                        "https://api.steampowered.com/ITwoFactorService/QueryTime/v1/",
                        content: null, ct).ConfigureAwait(false);
                    resp.EnsureSuccessStatusCode();
                    await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
                    if (doc.RootElement.TryGetProperty("response", out var r) &&
                        r.TryGetProperty("server_time", out var st) &&
                        long.TryParse(st.ToString(), out var s))
                        server = s;
                }
                catch { /* ignore */ }
            }

            var local = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (server > 0)
            {
                _diff = (int)(server - local);
                _checkedAt = DateTimeOffset.UtcNow;
                return local + _diff.Value;
            }
            return local;
        }
        finally { Gate.Release(); }
    }
}

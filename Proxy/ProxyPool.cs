using System.Net;

namespace TradeInventory.Proxy;

/// <summary>HTTP proxy with explicit NetworkCredential (URI userinfo alone causes 407 on CONNECT).</summary>
public sealed class ProxyEndpoint
{
    public required Uri Address { get; init; }
    public NetworkCredential? Credential { get; init; }
    public string Raw { get; init; } = "";

    public string Tag => Address.IsDefaultPort
        ? Address.Host
        : $"{Address.Host}:{Address.Port}";

    public WebProxy CreateWebProxy()
    {
        var proxy = new WebProxy(Address)
        {
            BypassProxyOnLocal = false,
            UseDefaultCredentials = false,
        };
        if (Credential is not null)
            proxy.Credentials = Credential;
        return proxy;
    }
}

public sealed class ProxyPool
{
    readonly List<ProxyEndpoint> _all;
    readonly List<int> _available;
    readonly HashSet<int> _inUse = new();
    readonly Dictionary<int, DateTimeOffset> _badUntil = new();
    readonly object _lock = new();
    readonly TimeSpan _badCooldown;

    public ProxyPool(IEnumerable<string> proxies, TimeSpan? badCooldown = null)
    {
        _all = proxies.Select(Parse).Where(p => p is not null).Cast<ProxyEndpoint>().ToList();
        if (_all.Count == 0) throw new InvalidOperationException("No proxies loaded");
        _available = Enumerable.Range(0, _all.Count).ToList();
        Shuffle(_available);
        _badCooldown = badCooldown ?? TimeSpan.FromMinutes(15);
    }

    public int Count => _all.Count;
    public int BadCount { get { lock (_lock) { Purge(); return _badUntil.Count; } } }

    public static List<string> LoadFile(string path)
    {
        return File.ReadAllLines(path)
            .Select(l => l.Trim().TrimStart('\uFEFF'))
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToList();
    }

    public (int Idx, ProxyEndpoint Proxy) Acquire(TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(120));
        while (true)
        {
            lock (_lock)
            {
                Purge();
                var good = _available.Where(i => !_badUntil.ContainsKey(i)).ToList();
                var pool = good.Count > 0 ? good : _available.ToList();
                if (pool.Count > 0)
                {
                    var pick = pool[Random.Shared.Next(pool.Count)];
                    _available.Remove(pick);
                    _inUse.Add(pick);
                    return (pick, _all[pick]);
                }
            }
            if (DateTimeOffset.UtcNow >= deadline)
                throw new TimeoutException("No free proxy available");
            Thread.Sleep(50);
        }
    }

    public void Release(int idx, bool bad = false)
    {
        lock (_lock)
        {
            _inUse.Remove(idx);
            if (bad)
            {
                _badUntil[idx] = DateTimeOffset.UtcNow + _badCooldown;
                return;
            }
            if (_badUntil.TryGetValue(idx, out var until) && until > DateTimeOffset.UtcNow)
                return;
            if (!_available.Contains(idx))
                _available.Insert(Random.Shared.Next(_available.Count + 1), idx);
        }
    }

    public string Tag(ProxyEndpoint proxy) => proxy.Tag;

    void Purge()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var i in _badUntil.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList())
        {
            _badUntil.Remove(i);
            if (!_inUse.Contains(i) && !_available.Contains(i))
                _available.Add(i);
        }
    }

    static ProxyEndpoint? Parse(string line)
    {
        try
        {
            string user, pass, hostPort;
            if (line.Contains("://", StringComparison.Ordinal))
            {
                var uri = new Uri(line);
                hostPort = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
                if (string.IsNullOrEmpty(uri.UserInfo))
                    return new ProxyEndpoint { Address = new Uri($"{uri.Scheme}://{hostPort}"), Raw = line };
                var ui = uri.UserInfo.Split(':', 2);
                user = Uri.UnescapeDataString(ui[0]);
                pass = ui.Length > 1 ? Uri.UnescapeDataString(ui[1]) : "";
            }
            else
            {
                var at = line.LastIndexOf('@');
                if (at <= 0) return null;
                var auth = line[..at];
                hostPort = line[(at + 1)..];
                var colon = auth.IndexOf(':');
                if (colon <= 0) return null;
                user = auth[..colon];
                pass = auth[(colon + 1)..];
            }

            if (!hostPort.Contains("://", StringComparison.Ordinal))
                hostPort = "http://" + hostPort;

            return new ProxyEndpoint
            {
                Address = new Uri(hostPort),
                Credential = new NetworkCredential(user, pass),
                Raw = line,
            };
        }
        catch { return null; }
    }

    static void Shuffle(List<int> list)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = Random.Shared.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}

public static class ProxyErrors
{
    public static bool IsProxyOrRate(Exception ex)
    {
        var s = ex.ToString().ToLowerInvariant();
        string[] markers =
        [
            "407", "proxy authentication", "429", "too many requests", "proxy", "tunnel",
            "timed out", "timeout", "connection reset", "connection refused", "ssl",
            "502", "503", "504", "500", "temporarily unavailable", "node connect",
            "unable to connect", "ajaxop success=false", "proxy rate/reject"
        ];
        return markers.Any(s.Contains);
    }
}

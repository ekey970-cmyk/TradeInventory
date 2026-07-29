using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.Internal;
using TradeInventory.Models;
using TradeInventory.Proxy;

namespace TradeInventory.Steam;

/// <summary>
/// SteamKit2 login (ASF-compatible) + community web session.
/// CM auth → JWT access token → cookies steamLoginSecure="{steamid}||{accessToken}" (ArchiWebHandler.Init).
/// Web HTTP still goes through the assigned proxy.
/// </summary>
public sealed class SteamWebSession : IDisposable
{
    public const string Community = "https://steamcommunity.com";
    public const string Api = "https://api.steampowered.com";
    public const string Store = "https://store.steampowered.com";
    const int SessionIdBytes = 12; // ASF SessionIDLength=24 hex chars
    const int MinAccessTokenMinutes = 5;

    readonly CookieContainer _cookies = new();
    readonly HttpClientHandler _handler;
    readonly HttpClient _http;
    readonly string _sessionsDir;
    readonly object _gate = new();
    readonly bool _keepCm;

    SteamClient? _steamClient;
    CallbackManager? _callbacks;
    SteamUser? _steamUser;
    CancellationTokenSource? _callbackCts;
    Task? _callbackLoop;
    string? _accessToken;
    string? _refreshToken;
    bool _cmLoggedOn;
    string? _deviceId;

    public SteamAccount Account { get; }
    public ProxyEndpoint? Proxy { get; }
    public string UserAgent { get; }
    public string? AccessToken => _accessToken;
    public SteamClient? Client => _steamClient;
    public bool IsCmLoggedOn => _cmLoggedOn;

    public SteamWebSession(SteamAccount account, ProxyEndpoint? proxy, string sessionsDir, bool keepCmConnected = true)
    {
        Account = account;
        Proxy = proxy;
        _sessionsDir = sessionsDir;
        _keepCm = keepCmConnected;
        UserAgent = RandomUa();
        _handler = new HttpClientHandler
        {
            CookieContainer = _cookies,
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            UseCookies = true,
            Proxy = proxy?.CreateWebProxy(),
            UseProxy = proxy is not null,
            DefaultProxyCredentials = proxy?.Credential,
        };
        // Also set on handler.Proxy explicitly (needed for HTTPS CONNECT auth).
        if (proxy is not null && _handler.Proxy is WebProxy wp && proxy.Credential is not null)
            wp.Credentials = proxy.Credential;
        // ASF-style: every web call goes through WebLimiter (per-host).
        var limited = new WebLimitHandler(_handler);
        _http = new HttpClient(limited)
        {
            Timeout = TimeSpan.FromSeconds(45),
            DefaultRequestVersion = HttpVersion.Version11,
        };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
    }

    public HttpClient Http => _http;

    /// <summary>
    /// Confirmation client like DoctorMcKay node-steamcommunity:
    /// same proxy + cookies as the bot session, UA = okhttp/4.9.2 (Steam Android).
    /// </summary>
    public const string ConfirmUserAgent = "okhttp/4.9.2";

    public HttpClient CreateConfirmHttp()
    {
        var cookies = new CookieContainer();
        foreach (Cookie c in _cookies.GetAllCookies())
        {
            try { cookies.Add(new Cookie(c.Name, c.Value, c.Path, c.Domain) { Secure = c.Secure }); }
            catch { /* skip bad cookie */ }
        }
        var handler = new HttpClientHandler
        {
            CookieContainer = cookies,
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            UseCookies = true,
            Proxy = Proxy?.CreateWebProxy(),
            UseProxy = Proxy is not null,
            DefaultProxyCredentials = Proxy?.Credential,
        };
        if (Proxy is not null && handler.Proxy is WebProxy wp && Proxy.Credential is not null)
            wp.Credentials = Proxy.Credential;
        var http = new HttpClient(new WebLimitHandler(handler))
        {
            Timeout = TimeSpan.FromSeconds(60),
            DefaultRequestVersion = HttpVersion.Version11,
        };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", ConfirmUserAgent);
        return http;
    }

    /// <summary>
    /// Direct (no-proxy) client with the same cookies — for mobileconf.
    /// Shared proxy IPs often get 429 on /mobileconf/* while local IP works (ASF usually has clean IP).
    /// </summary>
    public HttpClient CreateDirectHttp(string? userAgent = null)
    {
        var cookies = new CookieContainer();
        foreach (Cookie c in _cookies.GetAllCookies())
        {
            try { cookies.Add(new Cookie(c.Name, c.Value, c.Path, c.Domain) { Secure = c.Secure }); }
            catch { /* skip bad cookie */ }
        }
        var handler = new HttpClientHandler
        {
            CookieContainer = cookies,
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            UseCookies = true,
            UseProxy = false,
            Proxy = null,
        };
        // SDA path: no WebLimitHandler — SteamAuth uses plain WebClient
        var http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(60),
            DefaultRequestVersion = HttpVersion.Version11,
        };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent ?? UserAgent);
        return http;
    }

    public string? Cookie(string name, string domainContains = "steamcommunity.com")
    {
        foreach (Cookie c in _cookies.GetAllCookies())
        {
            if (c.Name == name && c.Domain.Contains(domainContains, StringComparison.OrdinalIgnoreCase))
                return c.Value;
        }
        foreach (Cookie c in _cookies.GetAllCookies())
            if (c.Name == name) return c.Value;
        return null;
    }

    public async Task ProbeAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, Community + "/");
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var code = (int)resp.StatusCode;
        if (code == 429) throw new HttpRequestException("429");
        if (code is 500 or 502 or 503 or 504) throw new HttpRequestException($"HTTP {code}");
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"probe HTTP {code}");
    }

    public async Task EnsureLoggedInAsync(bool force = false, CancellationToken ct = default)
    {
        lock (_gate)
        {
            /* serialize login per session instance */
        }

        if (!force && TryLoadTokens() && HasFreshAccessToken() && InitWebCookiesFromAccessToken())
        {
            if (await SessionLooksValidAsync(ct).ConfigureAwait(false))
                return;
        }

        if (force)
        {
            InvalidateSavedSession();
            _accessToken = _refreshToken = null;
        }

        await LoginViaSteamKitAsync(ct).ConfigureAwait(false);
    }

    async Task LoginViaSteamKitAsync(CancellationToken ct)
    {
        await ConnectCmAsync(ct).ConfigureAwait(false);
        try
        {
            if (!string.IsNullOrEmpty(_refreshToken) || TryLoadTokens())
            {
                try
                {
                    await LogOnWithRefreshAsync(ct).ConfigureAwait(false);
                    await EnsureAccessTokenAsync(ct).ConfigureAwait(false);
                    if (!InitWebCookiesFromAccessToken())
                        throw new InvalidOperationException("failed to init web cookies");
                    await ResolveDeviceIdAsync(ct).ConfigureAwait(false);
                    SaveTokens();
                    return;
                }
                catch (Exception ex) when (IsAuthTokenFailure(ex))
                {
                    _refreshToken = _accessToken = null;
                    InvalidateSavedSession();
                }
            }

            await AuthWithCredentialsAsync(ct).ConfigureAwait(false);
            if (!InitWebCookiesFromAccessToken())
                throw new InvalidOperationException("failed to init web cookies after auth");
            await ResolveDeviceIdAsync(ct).ConfigureAwait(false);
            SaveTokens();
        }
        finally
        {
            // ASF keeps CM connected for the bot lifetime. Only drop it when not requested.
            if (!_keepCm)
                await DisconnectCmAsync().ConfigureAwait(false);
        }
    }

    async Task AuthWithCredentialsAsync(CancellationToken ct)
    {
        if (_steamClient is null || !_steamClient.IsConnected)
            await ConnectCmAsync(ct).ConfigureAwait(false);

        var details = new AuthSessionDetails
        {
            Username = Account.Username,
            Password = Account.Password,
            DeviceFriendlyName = $"TradeInventory/{Environment.MachineName}",
            IsPersistentSession = true,
            Authenticator = new SharedSecretAuthenticator(Account.SharedSecret),
            PlatformType = EAuthTokenPlatformType.k_EAuthTokenPlatformType_SteamClient,
            WebsiteID = "Client",
        };

        CredentialsAuthSession authSession;
        try
        {
            authSession = await _steamClient!.Authentication
                .BeginAuthSessionViaCredentialsAsync(details)
                .WaitAsync(ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"SteamKit BeginAuth failed for {Account.Username}: {ex.Message}", ex);
        }

        AuthPollResult poll;
        try
        {
            poll = await authSession.PollingWaitForResultAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"SteamKit auth poll failed for {Account.Username}: {ex.Message}", ex);
        }

        if (string.IsNullOrEmpty(poll.RefreshToken) || string.IsNullOrEmpty(poll.AccessToken))
            throw new InvalidOperationException($"SteamKit auth returned empty tokens for {Account.Username}");

        _refreshToken = poll.RefreshToken;
        _accessToken = poll.AccessToken;

        await LogOnWithRefreshAsync(ct).ConfigureAwait(false);
    }

    async Task LogOnWithRefreshAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_refreshToken))
            throw new InvalidOperationException("missing refresh token");
        if (_steamClient is null || _steamUser is null)
            throw new InvalidOperationException("SteamClient not ready");
        if (!_steamClient.IsConnected)
            await ConnectCmAsync(ct).ConfigureAwait(false);

        var loggedOn = new TaskCompletionSource<SteamUser.LoggedOnCallback>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = _callbacks!.Subscribe<SteamUser.LoggedOnCallback>(cb => loggedOn.TrySetResult(cb));

        _steamUser.LogOn(new SteamUser.LogOnDetails
        {
            Username = Account.Username,
            AccessToken = _refreshToken,
            ShouldRememberPassword = true,
            LoginID = (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue),
            MachineName = $"TradeInventory-{Environment.MachineName}",
            ClientLanguage = "english",
        });

        SteamUser.LoggedOnCallback result;
        try
        {
            result = await loggedOn.Task.WaitAsync(TimeSpan.FromSeconds(45), ct).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new InvalidOperationException($"SteamKit LogOn timeout for {Account.Username}", ex);
        }

        if (result.Result != EResult.OK)
            throw new InvalidOperationException($"SteamKit LogOn {result.Result} / {result.ExtendedResult} for {Account.Username}");

        _cmLoggedOn = true;
        if (result.ClientSteamID is not null && result.ClientSteamID.ConvertToUInt64() != 0)
        {
            // keep steam64 from server if CSV wrong
        }
    }

    async Task EnsureAccessTokenAsync(CancellationToken ct)
    {
        if (HasFreshAccessToken()) return;
        if (_steamClient is null || string.IsNullOrEmpty(_refreshToken))
            throw new InvalidOperationException("cannot refresh access token");

        var steamId = ResolveSteamId();
        AccessTokenGenerateResult gen;
        try
        {
            gen = await _steamClient.Authentication
                .GenerateAccessTokenForAppAsync(steamId, _refreshToken, allowRenewal: true)
                .WaitAsync(ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"GenerateAccessToken failed for {Account.Username}: {ex.Message}", ex);
        }

        if (string.IsNullOrEmpty(gen.AccessToken))
            throw new InvalidOperationException($"empty access token for {Account.Username}");

        _accessToken = gen.AccessToken;
        if (!string.IsNullOrEmpty(gen.RefreshToken))
            _refreshToken = gen.RefreshToken;
    }

    async Task ConnectCmAsync(CancellationToken ct)
    {
        if (_steamClient?.IsConnected == true) return;

        await DisconnectCmAsync().ConfigureAwait(false);

        var proxy = Proxy;
        var config = SteamConfiguration.Create(builder =>
        {
            builder.WithConnectionTimeout(TimeSpan.FromSeconds(30));
            builder.WithHttpClientFactory(_ =>
            {
                var h = new HttpClientHandler
                {
                    AutomaticDecompression = DecompressionMethods.All,
                    Proxy = proxy?.CreateWebProxy(),
                    UseProxy = proxy is not null,
                    DefaultProxyCredentials = proxy?.Credential,
                };
                if (proxy is not null && h.Proxy is WebProxy wp && proxy.Credential is not null)
                    wp.Credentials = proxy.Credential;
                return new HttpClient(h) { Timeout = TimeSpan.FromSeconds(40) };
            });
        });

        _steamClient = new SteamClient(config);
        _callbacks = new CallbackManager(_steamClient);
        _steamUser = _steamClient.GetHandler<SteamUser>()!;

        _callbackCts = new CancellationTokenSource();
        var loopToken = _callbackCts.Token;
        var mgr = _callbacks;
        _callbackLoop = Task.Run(async () =>
        {
            while (!loopToken.IsCancellationRequested)
            {
                try
                {
                    await mgr.RunWaitCallbackAsync(loopToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch { /* keep pumping */ }
            }
        }, CancellationToken.None);

        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var c1 = _callbacks.Subscribe<SteamClient.ConnectedCallback>(_ => connected.TrySetResult());
        using var c2 = _callbacks.Subscribe<SteamClient.DisconnectedCallback>(cb =>
        {
            _cmLoggedOn = false;
            disconnected.TrySetResult(cb.UserInitiated ? "user" : "remote");
        });

        _steamClient.Connect();

        var finished = await Task.WhenAny(
            connected.Task,
            disconnected.Task,
            Task.Delay(TimeSpan.FromSeconds(35), ct)).ConfigureAwait(false);

        if (finished == disconnected.Task)
            throw new HttpRequestException($"SteamKit disconnected before connect ({await disconnected.Task.ConfigureAwait(false)})");
        if (finished != connected.Task)
            throw new HttpRequestException("SteamKit connect timeout");
    }

    async Task DisconnectCmAsync()
    {
        _cmLoggedOn = false;
        try
        {
            _steamUser?.LogOff();
        }
        catch { /* ignore */ }

        try
        {
            _steamClient?.Disconnect();
        }
        catch { /* ignore */ }

        if (_callbackCts is not null)
        {
            try { _callbackCts.Cancel(); } catch { /* ignore */ }
        }

        if (_callbackLoop is not null)
        {
            try { await Task.WhenAny(_callbackLoop, Task.Delay(1500)).ConfigureAwait(false); }
            catch { /* ignore */ }
        }

        _callbackCts?.Dispose();
        _callbackCts = null;
        _callbackLoop = null;
        _steamUser = null;
        _callbacks = null;
        _steamClient = null;
    }


    /// <summary>ASF: real device_identifier from TwoFactor.QueryStatus; fallback to derived android UUID.</summary>
    public async Task<string> ResolveDeviceIdAsync(CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(_deviceId))
            return _deviceId;

        var fetched = await TryFetchDeviceIdFromCmAsync(ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(fetched))
        {
            _deviceId = fetched;
            SaveTokens();
            return _deviceId!;
        }

        if (!string.IsNullOrEmpty(_deviceId))
            return _deviceId;

        _deviceId = SteamGuard.DeviceId(Account.Steam64Id);
        return _deviceId;
    }

    async Task<string?> TryFetchDeviceIdFromCmAsync(CancellationToken ct)
    {
        var broughtUp = false;
        try
        {
            if (_steamClient?.IsConnected != true || !_cmLoggedOn)
            {
                if (string.IsNullOrEmpty(_refreshToken) && !TryLoadTokens())
                    return null;
                await ConnectCmAsync(ct).ConfigureAwait(false);
                await LogOnWithRefreshAsync(ct).ConfigureAwait(false);
                broughtUp = true;
            }

            var unified = _steamClient!.GetHandler<SteamUnifiedMessages>();
            if (unified is null) return null;
            var svc = unified.CreateService<TwoFactor>();
            var req = new CTwoFactor_Status_Request { steamid = ResolveSteamId().ConvertToUInt64() };
            var resp = await svc.QueryStatus(req).ToTask().WaitAsync(ct).ConfigureAwait(false);
            if (resp.Result == EResult.OK && !string.IsNullOrEmpty(resp.Body.device_identifier))
            {
                Log.Info($"      device_id from SteamKit2 TwoFactor for {Account.Username}");
                return resp.Body.device_identifier;
            }
        }
        catch (Exception ex)
        {
            Log.Info($"      device_id QueryStatus failed: {ex.Message}");
        }
        finally
        {
            if (broughtUp && !_keepCm)
            {
                try { await DisconnectCmAsync().ConfigureAwait(false); } catch { /* ignore */ }
            }
        }
        return null;
    }

    bool InitWebCookiesFromAccessToken()
    {
        if (string.IsNullOrEmpty(_accessToken)) return false;
        var steamId = ResolveSteamId().ConvertToUInt64();
        if (steamId == 0) return false;

        // Exact ASF ArchiWebHandler.Init format
        var steamLoginSecure = $"{steamId}||{_accessToken}";
        var sessionId = Convert.ToHexString(RandomNumberGenerator.GetBytes(SessionIdBytes)).ToLowerInvariant();
        var tz = $"{(int)DateTimeOffset.Now.Offset.TotalSeconds},{0}";

        ClearCookies();
        foreach (var host in new[]
                 {
                     "steamcommunity.com", "store.steampowered.com",
                     "checkout.steampowered.com", "help.steampowered.com",
                     "api.steampowered.com", "steam.tv"
                 })
        {
            TryAddCookie("sessionid", sessionId, host);
            TryAddCookie("steamLoginSecure", steamLoginSecure, host, secure: true);
            TryAddCookie("timezoneOffset", tz, host);
            // Same as trade_steam/steam_login_fix — needed for mobileconf/trade flows
            TryAddCookie("mobileClient", "android", host);
            TryAddCookie("mobileClientVersion", "777777 3.6.1", host);
        }
        return !string.IsNullOrEmpty(Cookie("steamLoginSecure"));
    }

    void TryAddCookie(string name, string value, string domain, bool secure = false)
    {
        try
        {
            var c = new Cookie(name, value, "/", "." + domain) { Secure = secure };
            _cookies.Add(c);
        }
        catch
        {
            try { _cookies.Add(new Cookie(name, value, "/", domain) { Secure = secure }); }
            catch { /* skip */ }
        }
    }

    public async Task<bool> SessionLooksValidPublicAsync(CancellationToken ct) => await SessionLooksValidAsync(ct).ConfigureAwait(false);

    async Task<bool> SessionLooksValidAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(Cookie("steamLoginSecure"))) return false;
        var url = $"{Community}/inventory/{Account.Steam64Id}/730/2?l=english&count=1";
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if ((int)resp.StatusCode is 301 or 302 or 401 or 403) return false;
        if (!resp.IsSuccessStatusCode) return false;
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return text.Contains("success", StringComparison.Ordinal) || text.Contains("assets", StringComparison.Ordinal);
    }

    SteamID ResolveSteamId()
    {
        if (ulong.TryParse(Account.Steam64Id, out var id) && id > 0)
            return new SteamID(id);
        if (_steamClient?.SteamID is not null)
            return _steamClient.SteamID;
        throw new InvalidOperationException($"invalid steam64 for {Account.Username}");
    }

    bool HasFreshAccessToken()
    {
        if (string.IsNullOrEmpty(_accessToken)) return false;
        var exp = ReadJwtExp(_accessToken);
        if (exp is null) return true; // opaque — try it
        return exp.Value > DateTimeOffset.UtcNow.AddMinutes(MinAccessTokenMinutes);
    }

    static DateTimeOffset? ReadJwtExp(string jwt)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2) return null;
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            switch (payload.Length % 4)
            {
                case 2: payload += "=="; break;
                case 3: payload += "="; break;
            }
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
            if (!doc.RootElement.TryGetProperty("exp", out var exp)) return null;
            return DateTimeOffset.FromUnixTimeSeconds(exp.GetInt64());
        }
        catch { return null; }
    }

    static bool IsAuthTokenFailure(Exception ex)
    {
        var s = ex.ToString();
        // Do not treat proxy failures as bad tokens — keep refresh_token and rotate proxy.
        if (ProxyErrors.IsProxyOrRate(ex) &&
            !s.Contains("InvalidPassword", StringComparison.OrdinalIgnoreCase) &&
            !s.Contains("AccessDenied", StringComparison.OrdinalIgnoreCase) &&
            !s.Contains("Expired", StringComparison.OrdinalIgnoreCase))
            return false;
        return s.Contains("InvalidPassword", StringComparison.OrdinalIgnoreCase)
               || s.Contains("AccessDenied", StringComparison.OrdinalIgnoreCase)
               || s.Contains("Expired", StringComparison.OrdinalIgnoreCase)
               || s.Contains("InvalidToken", StringComparison.OrdinalIgnoreCase)
               || (s.Contains("LogOn", StringComparison.OrdinalIgnoreCase) &&
                   (s.Contains("InvalidPassword", StringComparison.OrdinalIgnoreCase) ||
                    s.Contains("AccessDenied", StringComparison.OrdinalIgnoreCase) ||
                    s.Contains("Expired", StringComparison.OrdinalIgnoreCase)));
    }

    string SessionPath()
    {
        Directory.CreateDirectory(_sessionsDir);
        var safe = Regex.Replace(Account.Username, @"[^\w.\-]+", "_");
        return Path.Combine(_sessionsDir, safe + ".json");
    }

    bool TryLoadTokens()
    {
        var path = SessionPath();
        if (!File.Exists(path)) return false;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.TryGetProperty("refresh_token", out var rt))
                _refreshToken = rt.GetString();
            if (root.TryGetProperty("access_token", out var at))
                _accessToken = at.GetString();
            if (root.TryGetProperty("device_id", out var did))
                _deviceId = did.GetString();
            // legacy cookie sessions: ignore, force SteamKit re-login
            return !string.IsNullOrEmpty(_refreshToken) || HasFreshAccessToken();
        }
        catch { return false; }
    }

    void SaveTokens()
    {
        var payload = new
        {
            refresh_token = _refreshToken,
            access_token = _accessToken,
            device_id = _deviceId,
            steam64 = Account.Steam64Id,
            saved_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            user_agent = UserAgent,
            backend = "steamkit2",
        };
        File.WriteAllText(SessionPath(),
            JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    public void InvalidateSavedSession()
    {
        var path = SessionPath();
        if (File.Exists(path)) File.Delete(path);
    }

    void ClearCookies()
    {
        foreach (Cookie c in _cookies.GetAllCookies())
            c.Expired = true;
    }

    static string RandomUa()
    {
        var majors = new[] { 120, 122, 124, 126, 128, 130, 131, 132, 133 };
        var major = majors[Random.Shared.Next(majors.Length)];
        var oss = new[]
        {
            "Windows NT 10.0; Win64; x64",
            "Windows NT 11.0; Win64; x64",
            "Macintosh; Intel Mac OS X 10_15_7",
            "X11; Linux x86_64",
        };
        var os = oss[Random.Shared.Next(oss.Length)];
        return $"Mozilla/5.0 ({os}) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{major}.0.0.0 Safari/537.36";
    }

    public void Dispose()
    {
        try { DisconnectCmAsync().GetAwaiter().GetResult(); } catch { /* ignore */ }
        // HttpClient owns WebLimitHandler -> HttpClientHandler
        _http.Dispose();
    }
}

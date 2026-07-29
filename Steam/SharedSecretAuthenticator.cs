using SteamKit2.Authentication;

namespace TradeInventory.Steam;

/// <summary>ASF-style IAuthenticator backed by steam_shared_secret.</summary>
public sealed class SharedSecretAuthenticator : IAuthenticator
{
    readonly string _sharedSecret;

    public SharedSecretAuthenticator(string sharedSecret) =>
        _sharedSecret = sharedSecret ?? throw new ArgumentNullException(nameof(sharedSecret));

    public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect)
    {
        // Brief pause if Steam rejected previous TOTP (clock skew / reuse).
        if (previousCodeWasIncorrect)
            return DelayedCodeAsync();
        return Task.FromResult(SteamGuard.GenerateCode(_sharedSecret));
    }

    public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect) =>
        Task.FromException<string>(new NotSupportedException(
            "Email Steam Guard is not supported; account must use mobile authenticator."));

    public Task<bool> AcceptDeviceConfirmationAsync() => Task.FromResult(false);

    async Task<string> DelayedCodeAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        return SteamGuard.GenerateCode(_sharedSecret);
    }
}

using System.Security.Cryptography;
using System.Text;

namespace TradeInventory.Steam;

/// <summary>Steam Guard TOTP + confirmation HMAC — same math as ASF MobileAuthenticator.</summary>
public static class SteamGuard
{
    const string Alphabet = "23456789BCDFGHJKMNPQRTVWXY";

    public static string GenerateCode(string sharedSecret, long? unixTime = null)
    {
        var key = DecodeSecret(sharedSecret);
        var ts = (unixTime ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()) / 30;
        Span<byte> timeBytes = stackalloc byte[8];
        for (var i = 7; i >= 0; i--)
        {
            timeBytes[i] = (byte)(ts & 0xff);
            ts >>= 8;
        }
        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(key, timeBytes, hash);
        var start = hash[^1] & 0x0f;
        var fullCode = ((hash[start] & 0x7f) << 24)
                       | ((hash[start + 1] & 0xff) << 16)
                       | ((hash[start + 2] & 0xff) << 8)
                       | (hash[start + 3] & 0xff);
        var chars = new char[5];
        for (var i = 0; i < 5; i++)
        {
            chars[i] = Alphabet[(int)(fullCode % Alphabet.Length)];
            fullCode /= Alphabet.Length;
        }
        return new string(chars);
    }

    /// <summary>ASF GenerateConfirmationHash(time, tag).</summary>
    public static string ConfirmationHash(string identitySecret, long time, string tag)
    {
        var key = DecodeSecret(identitySecret);
        var tagBytes = Encoding.UTF8.GetBytes(tag ?? "");
        var buf = new byte[8 + Math.Min(32, tagBytes.Length)];
        for (var i = 7; i >= 0; i--)
        {
            buf[i] = (byte)(time & 0xff);
            time >>= 8;
        }
        if (tagBytes.Length > 0)
            Buffer.BlockCopy(tagBytes, 0, buf, 8, Math.Min(32, tagBytes.Length));
        return Convert.ToBase64String(HMACSHA1.HashData(key, buf));
    }

    /// <summary>ASF device id: android:&lt;sha1(android:steam64)&gt; as UUID-ish.</summary>
    public static string DeviceId(string steam64)
    {
        var digest = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes($"android:{steam64}"))).ToLowerInvariant();
        return $"android:{digest[..8]}-{digest[8..12]}-{digest[12..16]}-{digest[16..20]}-{digest[20..32]}";
    }

    static byte[] DecodeSecret(string secret)
    {
        secret = secret.Trim();
        try { return Convert.FromBase64String(secret); }
        catch
        {
            // some exports are raw
            return Convert.FromBase64String(secret.PadRight((secret.Length + 3) / 4 * 4, '='));
        }
    }
}

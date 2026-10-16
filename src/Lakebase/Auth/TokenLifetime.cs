using System.Globalization;
using System.Text.Json;

namespace Lakebase;

/// <summary>
/// Reads token expiry from server responses instead of assuming a fixed lifetime.
/// Falls back conservatively when the field is absent.
/// </summary>
public static class TokenLifetime
{
    public static readonly TimeSpan Fallback = TimeSpan.FromMinutes(45);

    /// <summary>OAuth token endpoint: <c>expires_in</c> seconds.</summary>
    public static DateTime ExpiryFromExpiresIn(JsonElement root, DateTime utcNow)
    {
        if (root.TryGetProperty("expires_in", out var exp))
        {
            if (exp.ValueKind == JsonValueKind.Number && exp.TryGetInt64(out var seconds) && seconds > 0)
                return utcNow.AddSeconds(seconds);
            if (exp.ValueKind == JsonValueKind.String && long.TryParse(exp.GetString(), out seconds) && seconds > 0)
                return utcNow.AddSeconds(seconds);
        }

        return utcNow + Fallback;
    }

    /// <summary>Lakebase credential: <c>expire_time</c> (RFC 3339), else <c>expires_in</c>.</summary>
    public static DateTime ExpiryFromCredential(JsonElement root, DateTime utcNow)
    {
        if (root.TryGetProperty("expire_time", out var expireTime)
            && expireTime.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(
                expireTime.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed)
            && parsed.UtcDateTime > utcNow)
        {
            return parsed.UtcDateTime;
        }

        return ExpiryFromExpiresIn(root, utcNow);
    }
}

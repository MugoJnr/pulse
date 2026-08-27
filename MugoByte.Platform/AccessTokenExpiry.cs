using System.Text;
using System.Text.Json;

namespace MugoByte.Platform;

/// <summary>
/// Reads JWT <c>exp</c> from the payload segment as an expiry hint only.
/// Does not verify signatures — never use this for authorization decisions.
/// </summary>
public static class AccessTokenExpiry
{
    public static bool TryReadExp(string? accessToken, out DateTimeOffset expiresAt)
    {
        expiresAt = default;
        if (string.IsNullOrWhiteSpace(accessToken)) return false;

        try
        {
            var parts = accessToken.Split('.');
            if (parts.Length < 2) return false;

            var payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
            using var doc = JsonDocument.Parse(payloadJson);
            if (!doc.RootElement.TryGetProperty("exp", out var expEl))
                return false;

            long seconds;
            if (expEl.ValueKind == JsonValueKind.Number)
            {
                if (!expEl.TryGetInt64(out seconds))
                    seconds = (long)expEl.GetDouble();
            }
            else if (expEl.ValueKind == JsonValueKind.String
                     && long.TryParse(expEl.GetString(), out var parsed))
            {
                seconds = parsed;
            }
            else
            {
                return false;
            }

            expiresAt = DateTimeOffset.FromUnixTimeSeconds(seconds);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static byte[] Base64UrlDecode(string input)
    {
        var s = input.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }
}

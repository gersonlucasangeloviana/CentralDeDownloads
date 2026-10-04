using System.Security.Cryptography;
using System.Text;

namespace GeradorExcel.Core;

public sealed class DownloadLinks(string secret)
{
    private readonly byte[] _secret = Encoding.UTF8.GetBytes(secret);

    public string Create(string jobId, DateTime expiresAt)
    {
        var payload = $"{jobId}.{new DateTimeOffset(expiresAt).ToUnixTimeSeconds()}.{Guid.NewGuid():N}";
        var data = Encoding.UTF8.GetBytes(payload);
        var signature = HMACSHA256.HashData(_secret, data);
        return $"{Base64Url(data)}.{Base64Url(signature)}";
    }

    public string? Validate(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 2) return null;
        try
        {
            var data = Decode(parts[0]);
            var signature = Decode(parts[1]);
            var expected = HMACSHA256.HashData(_secret, data);
            if (!CryptographicOperations.FixedTimeEquals(signature, expected)) return null;
            var fields = Encoding.UTF8.GetString(data).Split('.');
            if (fields.Length != 3 || !long.TryParse(fields[1], out var expires)) return null;
            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= expires) return null;
            return fields[0];
        }
        catch (FormatException) { return null; }
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=')
        .Replace('+', '-').Replace('/', '_');

    private static byte[] Decode(string value)
    {
        var text = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(text.PadRight((text.Length + 3) / 4 * 4, '='));
    }
}

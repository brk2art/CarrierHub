using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CarrierHub.CarrierGateway.Webhooks;

// HMAC-SHA256 over "{timestamp}.{body}", sent as "ParcelGo-Signature: t=<unix seconds>,v1=<hex>".
public static class ParcelGoSignature
{
    public const string Header = "ParcelGo-Signature";

    public static string Compute(string secret, long timestamp, string body) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{body}")));

    public static bool IsValid(string? header, string body, string secret, DateTimeOffset now, TimeSpan tolerance)
    {
        if (string.IsNullOrWhiteSpace(header))
            return false;

        long? timestamp = null;
        string? signature = null;

        foreach (var part in header.Split(','))
        {
            var pair = part.Split('=', 2);
            if (pair.Length != 2)
                continue;

            switch (pair[0].Trim())
            {
                case "t" when long.TryParse(pair[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var t):
                    timestamp = t;
                    break;
                case "v1":
                    signature = pair[1].Trim().ToLowerInvariant();
                    break;
            }
        }

        if (timestamp is null || signature is null)
            return false;

        // Replay protection: a genuine webhook captured earlier can't be sent again later.
        if (Math.Abs(now.ToUnixTimeSeconds() - timestamp.Value) > tolerance.TotalSeconds)
            return false;

        var expected = Compute(secret, timestamp.Value, body);

        // Constant time: a normal compare stops at the first wrong character, and the timing would leak how much was right.
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(signature));
    }
}
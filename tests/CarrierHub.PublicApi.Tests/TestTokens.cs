using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace CarrierHub.PublicApi.Tests;

// Tokens signed with a key that exists only while the tests run.
public static class TestTokens
{
    public const string Issuer = "carrierhub-tests";
    public const string Audience = "carrierhub-public-api";
    public static readonly SymmetricSecurityKey Key = new(RandomNumberGenerator.GetBytes(32));

    public static string Create(string? merchantId, params string[] scopes)
    {
        var claims = new Dictionary<string, object>();
        if (merchantId is not null)
            claims[Claims.MerchantId] = merchantId;
        if (scopes.Length > 0)
            claims["scope"] = scopes;   // a JSON array, the same shape dotnet user-jwts produces

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Claims = claims,
            Expires = DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = new SigningCredentials(Key, SecurityAlgorithms.HmacSha256),
        });
    }
}
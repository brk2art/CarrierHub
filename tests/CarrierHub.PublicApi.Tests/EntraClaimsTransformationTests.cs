using System.Security.Claims;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace CarrierHub.PublicApi.Tests;

// Entra identities as the Entra scheme hands them over, translated into CarrierHub's claims.
public class EntraClaimsTransformationTests
{
    private static readonly IConfiguration Config = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Entra:MerchantUsers:0:ObjectId"] = "user-oid",
            ["Entra:MerchantUsers:0:MerchantId"] = "merchant-1",
            ["Entra:MerchantUsers:1:ObjectId"] = "sweep-job-oid",
            ["Entra:MerchantUsers:1:MerchantId"] = "merchant-1",
        })
        .Build();

    private static async Task<ClaimsPrincipal> TransformAsync(params Claim[] claims) =>
        await new EntraClaimsTransformation(Config).TransformAsync(
            new ClaimsPrincipal(new ClaimsIdentity(claims, Authentication.EntraScheme)));

    private static string[] Scopes(ClaimsPrincipal p) => p.FindAll("scope").Select(c => c.Value).ToArray();

    [Fact]
    public async Task A_program_with_the_read_app_role_can_read_but_not_write()
    {
        var p = await TransformAsync(new Claim("oid", "sweep-job-oid"), new Claim("roles", "Shipments.Read"));

        Assert.Equal("merchant-1", p.FindFirst(Claims.MerchantId)?.Value);
        Assert.Equal([CarrierHub.PublicApi.Scopes.Read], Scopes(p));
    }

    [Fact]
    public async Task A_user_still_gets_the_scopes_they_consented_to()
    {
        var p = await TransformAsync(new Claim("oid", "user-oid"), new Claim("scp", "Shipments.Read Shipments.Write"));

        Assert.Equal("merchant-1", p.FindFirst(Claims.MerchantId)?.Value);
        Assert.Equal([CarrierHub.PublicApi.Scopes.Read, CarrierHub.PublicApi.Scopes.Write], Scopes(p));
    }

    [Fact]
    public async Task A_program_not_mapped_to_a_merchant_gets_no_merchant()
    {
        var p = await TransformAsync(new Claim("oid", "some-other-app"), new Claim("roles", "Shipments.Read"));

        Assert.Null(p.FindFirst(Claims.MerchantId));   // so every policy refuses it: never a default merchant
    }

    [Fact]
    public async Task An_unknown_role_gives_nothing()
    {
        var p = await TransformAsync(new Claim("oid", "sweep-job-oid"), new Claim("roles", "Shipments.Delete"));

        Assert.Empty(Scopes(p));
    }
}
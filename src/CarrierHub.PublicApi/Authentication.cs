using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.JsonWebTokens;

namespace CarrierHub.PublicApi;

// Who may call the Public API. Two kinds of tokens, each validated completely by its own scheme:
//   "Bearer": development tokens from dotnet user-jwts (settings in appsettings.Development.json), as before;
//   "Entra":  Microsoft Entra ID tokens issued FOR this API (audience = its client ID), e.g. obtained by the MCP
//             server on behalf of a signed-in user.
// Entra tokens are translated into CarrierHub's claims (merchant, scopes), so every policy and endpoint stays the same.
public static class Authentication
{
    public const string EntraScheme = "Entra";
    private const string ChooserScheme = "BearerOrEntra";

    public static void AddCarrierHubAuthentication(this WebApplicationBuilder builder)
    {
        var tenantId = builder.Configuration["Entra:TenantId"];
        var clientId = builder.Configuration["Entra:PublicApi:ClientId"];

        // Without Entra settings (tests, CI): development tokens only, exactly as before.
        if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(clientId))
        {
            builder.Services.AddAuthentication().AddJwtBearer();
            return;
        }

        builder.Services.AddAuthentication(ChooserScheme)
            .AddJwtBearer() // "Bearer", configured from appsettings as before
            .AddJwtBearer(EntraScheme, o =>
            {
                // The issuer and the signing keys come from Entra's published metadata for this tenant.
                o.Authority = $"https://login.microsoftonline.com/{tenantId}/v2.0";
                // Accept only tokens issued FOR THIS API. A token for the MCP server is refused here: no passthrough.
                o.TokenValidationParameters.ValidAudiences = [clientId, $"api://{clientId}"];
                // Keep Entra's own claim names (scp, oid) instead of .NET's long URIs.
                o.MapInboundClaims = false;
                // Marks identities from this scheme, so the transformation below knows what to translate.
                o.TokenValidationParameters.AuthenticationType = EntraScheme;
            })
            // Only CHOOSES a scheme: it reads the issuer without trusting it. The chosen scheme validates everything,
            // so a token that lies about its issuer just reaches a scheme that rejects it.
            .AddPolicyScheme(ChooserScheme, "Development or Entra tokens", o =>
            {
                o.ForwardDefaultSelector = context =>
                {
                    var header = context.Request.Headers.Authorization.ToString();
                    if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    {
                        var raw = header["Bearer ".Length..].Trim();
                        var handler = new JsonWebTokenHandler();
                        if (handler.CanReadToken(raw)
                            && handler.ReadJsonWebToken(raw).Issuer.StartsWith(
                                "https://login.microsoftonline.com/", StringComparison.OrdinalIgnoreCase))
                            return EntraScheme;
                    }
                    return JwtBearerDefaults.AuthenticationScheme;
                };
            });

        builder.Services.AddTransient<IClaimsTransformation, EntraClaimsTransformation>();
    }
}

// Translates a validated Entra identity into CarrierHub's claims. Entra knows WHO the user is (oid); which merchant
// they act for is CarrierHub's business, so it comes from CarrierHub's own mapping (Entra:Merchants:<oid>).
// In a multitenant SaaS this would map the customer's directory (the "tid" claim) instead of one user.
public sealed class EntraClaimsTransformation(IConfiguration config): IClaimsTransformation
{
    // Entra's scope names → CarrierHub's.
    private static readonly Dictionary<string, string> ScopeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Shipments.Read"] = Scopes.Read,
        ["Shipments.Write"] = Scopes.Write
    };

    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        // Only Entra identities, and only once (ASP.NET Core may call this more than once per request).
        if (principal.Identity is not ClaimsIdentity { AuthenticationType: Authentication.EntraScheme } identity
            || identity.HasClaim(c => c.Type == Claims.MerchantId))
            return Task.FromResult(principal);

        var oid = identity.FindFirst("oid")?.Value;
        var merchantId = oid is null ? null : config[$"Entra:Merchants:{oid}"];

        // A user with no merchant gets no merchant claim, so every policy refuses them (403). Never a default.
        if (merchantId is null)
            return Task.FromResult(principal);

        var translated = new ClaimsIdentity(identity.Claims, identity.AuthenticationType);
        translated.AddClaim(new Claim(Claims.MerchantId, merchantId));

        // "scp" is one space-separated string: "Shipments.Read Shipments.Write".
        foreach (var scope in (identity.FindFirst("scp")?.Value ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (ScopeMap.TryGetValue(scope, out var carrierHubScope))
                translated.AddClaim(new Claim("scope", carrierHubScope));

        // Who, for /v1/me and the audit trail: the sign-in name when Entra sends one, else the stable object ID.
        var who = identity.FindFirst("preferred_username")?.Value ?? oid!;
        translated.AddClaim(new Claim(ClaimTypes.NameIdentifier, who));

        return Task.FromResult(new ClaimsPrincipal(translated));
    }
}

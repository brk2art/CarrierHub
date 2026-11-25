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
        var entraConfigured = !string.IsNullOrEmpty(tenantId) && !string.IsNullOrEmpty(clientId);

        // Development tokens are signed with a key on a developer's machine, so Production never accepts them.
        // Development and Testing (local runs, tests, CI) still do.
        var devTokensAllowed = !builder.Environment.IsProduction();

        // Without Entra settings: development tokens only (tests, CI). In Production that would mean
        // no valid sign-in at all, so refuse to start instead of running with nobody able to call the API.
        if (!entraConfigured)
        {
            if (!devTokensAllowed)
                throw new InvalidOperationException(
                    "Set Entra:TenantId and Entra:PublicApi:ClientId: development tokens are not accepted in Production.");

            builder.Services.AddAuthentication().AddJwtBearer();
            return;
        }

        var auth = builder.Services.AddAuthentication(devTokensAllowed ? ChooserScheme : EntraScheme)
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
            });

        // Outside Production, both kinds of tokens are accepted, and a chooser picks the scheme per request.
        if (devTokensAllowed)
        {
            auth.AddJwtBearer() // "Bearer", configured from appsettings as before
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
        }

        builder.Services.AddTransient<IClaimsTransformation, EntraClaimsTransformation>();
    }
}

// Translates a validated Entra identity into CarrierHub's claims. Entra knows WHO the user is (oid); which merchant
// they act for is CarrierHub's business, so it comes from CarrierHub's own mapping (Entra:MerchantUsers).
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
        var merchantId = oid is null ? null : MerchantFor(oid);

        // A user with no merchant gets no merchant claim, so every policy refuses them (403). Never a default.
        if (merchantId is null)
            return Task.FromResult(principal);

        var translated = new ClaimsIdentity(identity.Claims, identity.AuthenticationType);
        translated.AddClaim(new Claim(Claims.MerchantId, merchantId));

        // Users get scopes: "scp" is one space-separated string, from consent ("Shipments.Read Shipments.Write").
        // Programs get app roles: one "roles" claim per role assigned to them in Entra. Both translate the same
        // way, and anything unknown is ignored.
        var granted = (identity.FindFirst("scp")?.Value ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Concat(identity.FindAll("roles").Select(c => c.Value));
        foreach (var permission in granted)
            if (ScopeMap.TryGetValue(permission, out var carrierHubScope))
                translated.AddClaim(new Claim("scope", carrierHubScope));

        // Who, for /v1/me and the audit trail: the sign-in name when Entra sends one, else the stable object ID.
        var who = identity.FindFirst("preferred_username")?.Value ?? oid!;
        translated.AddClaim(new Claim(ClaimTypes.NameIdentifier, who));

        return Task.FromResult(new ClaimsPrincipal(translated));
    }

    // Entra:MerchantUsers is a list of { ObjectId, MerchantId } pairs. A list, not Entra:Merchants:<oid>, because in Azure
    // the object ID comes from a deployment parameter, and a parameter can fill a setting's value but not its name.
    private string? MerchantFor(string oid) =>
        config.GetSection("Entra:MerchantUsers").GetChildren()
            .FirstOrDefault(user => string.Equals(user["ObjectId"], oid, StringComparison.OrdinalIgnoreCase))?["MerchantId"];
}

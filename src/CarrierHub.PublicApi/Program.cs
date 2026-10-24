using System.Security.Claims;
using System.Threading.RateLimiting;
using CarrierHub.PublicApi;
using CarrierHub.PublicApi.V1;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddOpenApi();

// Validates bearer tokens with the settings under "Authentication:Schemes:Bearer".
// In development, dotnet user-jwts writes those settings and keeps the signing key in user secrets.
builder.Services.AddAuthentication().AddJwtBearer();

builder.Services.AddAuthorizationBuilder()
    // Reading needs a merchant and either scope; writing needs a merchant and the write scope.
    .AddPolicy(Policies.Read, p => p
        .RequireClaim(Claims.MerchantId)
        .RequireClaim("scope", Scopes.Read, Scopes.Write))
    .AddPolicy(Policies.Write, p => p
        .RequireClaim(Claims.MerchantId)
        .RequireClaim("scope", Scopes.Write));

// A merchant that floods the API slows down only itself: the bucket is keyed on the token's merchant, not the IP.
var limits = builder.Configuration.GetSection("RateLimits:PerMerchant").Get<MerchantRateLimit>() ?? new MerchantRateLimit();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(RateLimits.PerMerchant, context =>
        RateLimitPartition.GetTokenBucketLimiter(
            context.User.FindFirstValue(Claims.MerchantId) ?? "no-merchant",
            _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = limits.TokenLimit,
                TokensPerPeriod = limits.TokensPerPeriod,
                ReplenishmentPeriod = TimeSpan.FromSeconds(limits.PeriodSeconds),
                QueueLimit = 0,   // reject at once instead of holding requests
                AutoReplenishment = true,
            }));
    options.OnRejected = (context, _) =>
    {
        // Tell well-behaved clients when to come back.
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
        return ValueTask.CompletedTask;
    };
});

builder.Services.AddHttpClient<TrackingClient>(client => client.BaseAddress = new Uri("https+http://tracking"));
builder.Services.AddHttpClient<NotificationClient>(client => client.BaseAddress = new Uri("https+http://notification"));

var app = builder.Build();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();   // after authentication, so the merchant is known; rejected callers don't use up a bucket

app.MapGet("/", () => "CarrierHub Public API");
app.MapShipmentEndpoints();
app.MapWebhookEndpoints();
app.MapDefaultEndpoints();

app.Run();

public partial class Program { }   // for integration tests
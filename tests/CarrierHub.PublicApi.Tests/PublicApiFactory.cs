using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace CarrierHub.PublicApi.Tests;

public sealed class PublicApiFactory : WebApplicationFactory<Program>
{
    public FakeTracking Tracking { get; } = new();
    // The same kind of fake, standing in for the Notification service.
    public FakeTracking Notification { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Not "Development": no user secrets and no appsettings.Development.json, so the tests
        // don't depend on anything on your machine.
        builder.UseEnvironment("Testing");

        // A small bucket that doesn't refill during a test, so the limit is reached on purpose.
        builder.UseSetting("RateLimits:PerMerchant:TokenLimit", "3");
        builder.UseSetting("RateLimits:PerMerchant:TokensPerPeriod", "3");
        builder.UseSetting("RateLimits:PerMerchant:PeriodSeconds", "60");

        builder.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));

        builder.ConfigureTestServices(services =>
        {
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = TestTokens.Issuer,
                    ValidAudience = TestTokens.Audience,
                    IssuerSigningKey = TestTokens.Key,
                });

            // The fake replaces the whole client, retries included: these tests check our rules, not Polly's.
            services.RemoveAll<TrackingClient>();
            services.AddTransient(_ => new TrackingClient(
                new HttpClient(Tracking, disposeHandler: false) { BaseAddress = new Uri("http://tracking") }));

            services.RemoveAll<NotificationClient>();
            services.AddTransient(_ => new NotificationClient(
                new HttpClient(Notification, disposeHandler: false) { BaseAddress = new Uri("http://notification") }));
        });
    }
}
// RemoveAllResilienceHandlers may still be marked experimental in this version of Microsoft.Extensions.Http.Resilience.
#pragma warning disable EXTEXP0001

using CarrierHub.Notification.Api;
using CarrierHub.Notification.Data;
using CarrierHub.Notification.Webhooks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Http.Resilience;
using Polly;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddNpgsqlDbContext<NotificationDbContext>("notificationdb");
builder.Services.AddSingleton(TimeProvider.System);

// Merchant endpoints: a short retry and timeouts, but no circuit breaker. One breaker shared by all
// merchants would let one broken endpoint stop deliveries to everyone else.
var webhookClient = builder.Services.AddHttpClient<WebhookDeliverer>();
webhookClient.RemoveAllResilienceHandlers();
webhookClient.AddResilienceHandler("merchant-webhooks", pipeline =>
{
    pipeline.AddTimeout(TimeSpan.FromSeconds(15));   // everything, retries included
    pipeline.AddRetry(new HttpRetryStrategyOptions
    {
        MaxRetryAttempts = 2,
        Delay = TimeSpan.FromSeconds(1),
        BackoffType = DelayBackoffType.Exponential,
        UseJitter = true,
    });
    pipeline.AddTimeout(TimeSpan.FromSeconds(5));    // each attempt
});

// Delivery runs only with Service Bus (under Aspire); the endpoints work without it.
var hasMessaging = builder.Configuration.GetConnectionString("messaging") is not null;
if (hasMessaging)
{
    builder.AddAzureServiceBusClient("messaging");
    builder.Services.AddHostedService<NotificationConsumer>();
}

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<NotificationDbContext>().Database.MigrateAsync();
}

app.UseHttpsRedirection();

app.MapGet("/", () => "CarrierHub Notification");
app.MapWebhookEndpoints();
if (hasMessaging)
    app.MapDeadLetterReplay();
app.MapDefaultEndpoints();

app.Run();

public partial class Program { }   // for tests
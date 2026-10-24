// RemoveAllResilienceHandlers may still be marked experimental in this version of Microsoft.Extensions.Http.Resilience.
#pragma warning disable EXTEXP0001

using CarrierHub.CarrierGateway;
using CarrierHub.CarrierGateway.Adapters;
using CarrierHub.CarrierGateway.Webhooks;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddRedisClient("redis");   // connection string comes from Aspire
builder.AddAzureServiceBusClient("messaging");   // connection string comes from Aspire

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.Configure<PollingOptions>(builder.Configuration.GetSection("Polling"));
builder.Services.Configure<DedupeOptions>(builder.Configuration.GetSection("Dedupe"));

builder.Services.AddSingleton<GatewayMetrics>();
builder.Services.AddSingleton<ForwardedEvents>();
builder.Services.AddSingleton<CarrierRateLimiter>();
builder.Services.AddSingleton<CarrierEventPublisher>();

// Our own meter, next to the ASP.NET Core and HttpClient metrics that ServiceDefaults already exports.
builder.Services.AddOpenTelemetry().WithMetrics(metrics => metrics.AddMeter(GatewayMetrics.MeterName));

builder.Services.AddHttpClient<TrackingClient>(c => c.BaseAddress = new Uri("https+http://tracking"));

// Each carrier gets its own pipeline and limit, from "Carriers:{carrier}" in configuration.
AddCarrierClient<FastShipAdapter>(builder, "fastship");
AddCarrierClient<NordPostAdapter>(builder, "nordpost");

builder.Services.AddHostedService<CarrierPoller>();
// ParcelGo pushes instead of being polled: subscribe its shipments, receive its webhooks.
builder.Services.AddHttpClient<ParcelGoClient>(c => c.BaseAddress = new Uri("https+http://mockcarriers"));
builder.Services.AddHostedService<ParcelGoSubscriber>();

var app = builder.Build();

app.MapGet("/", () => "CarrierHub Carrier Gateway");   // ParcelGo's webhook endpoint comes in step 4

// The Gateway's counts since it started: what Run A and Run B compare.
app.MapGet("/stats", (GatewayMetrics metrics) =>
{
    var (published, skipped) = metrics.Totals;
    return new { published, skipped };
});
app.MapParcelGoWebhook();
app.MapDefaultEndpoints();

app.Run();

static void AddCarrierClient<TAdapter>(WebApplicationBuilder builder, string carrier)
    where TAdapter : class, ICarrierAdapter
{
    var section = builder.Configuration.GetSection($"Carriers:{carrier}");
    var perMinute = section.GetValue<int>("RequestsPerMinute");

    var client = builder.Services.AddHttpClient<TAdapter>(c => c.BaseAddress = new Uri("https+http://mockcarriers"));

    // Replace the shared default pipeline with this carrier's own: one carrier's failures
    // can't open another carrier's circuit, and each gets timeouts that fit how it behaves.
    client.RemoveAllResilienceHandlers();
    client.AddStandardResilienceHandler(section.GetSection("Resilience"));

    // Added after the pipeline, so it runs inside it: every attempt, retries included, counts against the limit.
    client.AddHttpMessageHandler(sp =>
        new CarrierRateLimitHandler(carrier, perMinute, sp.GetRequiredService<CarrierRateLimiter>()));

    builder.Services.AddTransient<ICarrierAdapter>(sp => sp.GetRequiredService<TAdapter>());
}

public partial class Program { }   // for tests
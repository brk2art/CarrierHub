using CarrierHub.CarrierGateway;
using CarrierHub.CarrierGateway.Adapters;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();   // includes retry with jitter, timeouts and a circuit breaker on every HttpClient
builder.AddRedisClient("redis");   // connection string comes from Aspire

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.Configure<PollingOptions>(builder.Configuration.GetSection("Polling"));
builder.Services.Configure<DedupeOptions>(builder.Configuration.GetSection("Dedupe"));

builder.Services.AddSingleton<GatewayMetrics>();
builder.Services.AddSingleton<ForwardedEvents>();

// Our own meter, next to the ASP.NET Core and HttpClient metrics that ServiceDefaults already exports.
builder.Services.AddOpenTelemetry().WithMetrics(metrics => metrics.AddMeter(GatewayMetrics.MeterName));


builder.Services.AddHttpClient<TrackingClient>(c => c.BaseAddress = new Uri("https+http://tracking"));

// One typed client per carrier, so Step 3 can give each carrier its own resilience settings and rate limit.
builder.Services.AddHttpClient<FastShipAdapter>(c => c.BaseAddress = new Uri("https+http://mockcarriers"));
builder.Services.AddHttpClient<NordPostAdapter>(c => c.BaseAddress = new Uri("https+http://mockcarriers"));
builder.Services.AddTransient<ICarrierAdapter>(sp => sp.GetRequiredService<FastShipAdapter>());
builder.Services.AddTransient<ICarrierAdapter>(sp => sp.GetRequiredService<NordPostAdapter>());

builder.Services.AddHostedService<CarrierPoller>();

var app = builder.Build();

app.MapGet("/", () => "CarrierHub Carrier Gateway");   // ParcelGo's webhook endpoint comes in step 4
// The Gateway's counts since it started: what Run A and Run B compare.
app.MapGet("/stats", (GatewayMetrics metrics) =>
{
    var (added, duplicates, skipped) = metrics.Totals;
    return new { added, duplicatesSent = duplicates, skipped };
});
app.MapDefaultEndpoints();

app.Run();

public partial class Program { }   // for tests
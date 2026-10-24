using CarrierHub.Tracking.Api;
using CarrierHub.Tracking.Application;
using CarrierHub.Tracking.Data;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddNpgsqlDbContext<TrackingDbContext>("trackingdb");   // connection string comes from Aspire
builder.Services.AddSingleton(TimeProvider.System);            // a fake clock in tests, the real one here
builder.Services.AddOpenApi();
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));   // "Delivered", not 5

// Carrier events arrive through Service Bus when it's configured (under Aspire). Without it, as in the
// integration tests, the consumer isn't started and the HTTP endpoint still accepts events.
if (builder.Configuration.GetConnectionString("messaging") is not null)
{
    builder.AddAzureServiceBusClient("messaging");
    builder.Services.AddHostedService<CarrierEventConsumer>();
}

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();

    // Development only: bring the database up to date at startup.
    // Production will use a migration bundle in the deployment pipeline (Phase 6).
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<TrackingDbContext>().Database.MigrateAsync();
}

app.UseHttpsRedirection();

app.MapGet("/", () => "CarrierHub Tracking");
app.MapShipmentEndpoints();
app.MapDefaultEndpoints();

app.Run();

public partial class Program { }   // lets the integration tests start this app
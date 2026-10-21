var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults(); // OpenTelemetry, health checks, service discovery, resilience
builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseHttpsRedirection();

app.MapGet("/", () => "CarrierHub Tracking");
app.MapDefaultEndpoints();      // /health and /alive

app.Run();

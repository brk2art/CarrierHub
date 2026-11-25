using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

// Adds common Aspire services: service discovery, resilience, health checks, and OpenTelemetry.
// This project should be referenced by each service project in your solution.
// To learn more about using this project, see https://aka.ms/aspire/service-defaults
public static class Extensions
{
    private const string HealthEndpointPath = "/health";
    private const string AlivenessEndpointPath = "/alive";
    private const string HealthCachePolicy = "health";

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        // Azure SDK tracing (Service Bus send and process spans) is still opt-in.
        AppContext.SetSwitch("Azure.Experimental.EnableActivitySource", true);

        builder.ConfigureOpenTelemetry();

        builder.AddDefaultHealthChecks();

        builder.Services.AddServiceDiscovery();

        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            // Turn on resilience by default
            http.AddStandardResilienceHandler();

            // Turn on service discovery by default
            http.AddServiceDiscovery();
        });

        // Uncomment the following to restrict the allowed schemes for service discovery.
        // builder.Services.Configure<ServiceDiscoveryOptions>(options =>
        // {
        //     options.AllowedSchemes = ["https"];
        // });

        // Don't announce the web server in every response ("server: Kestrel"). Not a fix for any bug, but one less
        // hint for an attacker, and security scans flag it.
        builder.Services.Configure<KestrelServerOptions>(o => o.AddServerHeader = false);

        return builder;
    }

    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    // Our own meters: outbox wait, webhook delivery time, gateway counters.
                    .AddMeter("CarrierHub.*");
            })
            .WithTracing(tracing =>
            {
                tracing.AddSource(builder.Environment.ApplicationName)
                    // Our own spans: the Gateway's shipment polls and the Tracking outbox publisher.
                    .AddSource("CarrierHub.*")
                    .AddSource("Azure.Messaging.ServiceBus.*")
                    .AddAspNetCoreInstrumentation(tracing =>
                        // Exclude health check requests from tracing
                        tracing.Filter = context =>
                            !context.Request.Path.StartsWithSegments(HealthEndpointPath)
                            && !context.Request.Path.StartsWithSegments(AlivenessEndpointPath)
                    )
                    // Uncomment the following line to enable gRPC instrumentation (requires the OpenTelemetry.Instrumentation.GrpcNetClient package)
                    //.AddGrpcClientInstrumentation()
                    .AddHttpClientInstrumentation();
            });

        builder.AddOpenTelemetryExporters();

        return builder;
    }

    private static TBuilder AddOpenTelemetryExporters<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        var useOtlpExporter = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

        if (useOtlpExporter)
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        // Uncomment the following lines to enable the Azure Monitor exporter (requires the Azure.Monitor.OpenTelemetry.AspNetCore package)
        if (!string.IsNullOrEmpty(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
        {
            builder.Services.AddOpenTelemetry()
               .UseAzureMonitor();
        }

        return builder;
    }

    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks()
            // Liveness: only "is this process responsive?". Never a dependency here: during a Postgres outage,
            // a failing liveness check would make Container Apps restart every replica at once.
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);
        // The readiness checks for Postgres, Redis and Service Bus come from the Aspire client integrations.

        // /health runs real checks against the dependencies, and on public apps anyone can call it.
        // Caching the result for 10 s caps the work a caller can cause at one run per 10 s per replica.
        builder.Services.AddOutputCache(o => o.AddPolicy(HealthCachePolicy, p => p.Expire(TimeSpan.FromSeconds(10))));

        // A health check must answer fast, not retry: Aspire's EF integration retries on failure, which is right for
        // queries but made the database check take 163 s while Postgres was down. 3 s, then it counts as Unhealthy.
        builder.Services.PostConfigure<HealthCheckServiceOptions>(o =>
        {
            foreach (var registration in o.Registrations)
                registration.Timeout = TimeSpan.FromSeconds(3);
        });

        return builder;
    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        // Mapped in every environment, because Container Apps probes them. What keeps that safe:
        //   - outside Development the response is only "Healthy", "Degraded" or "Unhealthy": no names, no errors;
        //   - /health is cached for 10 s, so calling it in a loop doesn't load the database;
        //   - /alive checks nothing but the process itself.
        app.UseOutputCache();

        // Readiness: every check must pass before the replica gets traffic.
        var readiness = new HealthCheckOptions();
        if (app.Environment.IsDevelopment())
            readiness.ResponseWriter = WriteDetailsAsync;   // locally: which check passed or failed, and how long it took
        app.MapHealthChecks(HealthEndpointPath, readiness).CacheOutput(HealthCachePolicy);

        // Liveness: only the checks tagged "live".
        app.MapHealthChecks(AlivenessEndpointPath, new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains("live")
        });

        return app;
    }

    // Development only: each check's name, status and duration. Never in Production, because names and
    // errors tell an attacker what the service depends on.
    private static Task WriteDetailsAsync(HttpContext context, HealthReport report) =>
        context.Response.WriteAsJsonAsync(new
        {
            status = report.Status.ToString(),
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                ms = (int)e.Value.Duration.TotalMilliseconds
            })
        });
}

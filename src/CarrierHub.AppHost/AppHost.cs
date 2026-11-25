using Aspire.Hosting.Azure;
using Azure.Provisioning.AppContainers;
using Azure.Provisioning.CognitiveServices;
using Azure.Provisioning.Search;

var builder = DistributedApplication.CreateBuilder(args);

// The Azure Container Apps environment is described here, not left to azd. That's needed for
// explicit role assignments (least privilege) and lets us configure the environment in C#.
builder.AddAzureContainerAppEnvironment("env");

// PostgreSQL: Azure Database for PostgreSQL Flexible Server in Azure, a container locally.
// Locally, the volume keeps data between runs.
var postgres = builder.AddAzurePostgresFlexibleServer("postgres")
    .RunAsContainer(pg => pg.WithDataVolume());

var trackingDb = postgres.AddDatabase("trackingdb");
var notificationDb = postgres.AddDatabase("notificationdb");
var assistantDb = postgres.AddDatabase("assistantdb");

// Redis: the Gateway's memory of which events it already forwarded. No volume on purpose:
// a restart starts empty, so every measurement run starts from the same point.
var redis = builder.AddRedis("redis");

// Azure Service Bus, locally as the emulator (the emulator plus a SQL Server it stores messages in).
var messaging = builder.AddAzureServiceBus("messaging").RunAsEmulator();

// Carrier events from the Gateway to Tracking. One session per shipment: Tracking handles a shipment's
// events one at a time, so they no longer race each other.
messaging.AddServiceBusQueue("carrier-events")
    .WithProperties(queue =>
    {
        queue.RequiresSession = true;
        queue.RequiresDuplicateDetection = true;                               // same MessageId within the window: dropped
        queue.DuplicateDetectionHistoryTimeWindow = TimeSpan.FromMinutes(5);   // the emulator's maximum; Azure allows more
        queue.MaxDeliveryCount = 10;                                           // then the dead-letter queue
        queue.DeadLetteringOnMessageExpiration = true;   // an expired message is parked in the dead-letter queue, not deleted
    });

// Shipment events from Tracking's outbox. Each subscription gets its own copy; one session per shipment
// keeps each shipment's messages in order.
var shipmentEvents = messaging.AddServiceBusTopic("shipment-events")
    .WithProperties(topic =>
    {
        topic.RequiresDuplicateDetection = true;
        topic.DuplicateDetectionHistoryTimeWindow = TimeSpan.FromMinutes(5);
    });

// For the Notification service.
shipmentEvents.AddServiceBusSubscription("notifications")
    .WithProperties(subscription =>
    {
        subscription.RequiresSession = true;
        subscription.MaxDeliveryCount = 5;                     // then the dead-letter queue
        subscription.DeadLetteringOnMessageExpiration = true;
    });

// Tracking owns shipments and their timeline. Only it talks to trackingdb.
var tracking = builder.AddProject<Projects.CarrierHub_Tracking>("tracking")
    .WithReference(trackingDb)
    .WaitFor(trackingDb)
    .WithReference(messaging)
    .WaitFor(messaging)
    .WithEnvironment("Database__MigrateOnStartup", "true")
    .WithHealthProbes();

// Delivers status changes to merchants' own webhook URLs. Owns the webhooks and the delivery log.
var notification = builder.AddProject<Projects.CarrierHub_Notification>("notification")
    .WithReference(notificationDb)
    .WaitFor(notificationDb)
    .WithReference(messaging)
    .WaitFor(messaging)
    .WithEnvironment("Database__MigrateOnStartup", "true")
    .WithHealthProbes();

// The merchant-facing API. It reaches shipment data only through Tracking.
var publicApi = builder.AddProject<Projects.CarrierHub_PublicApi>("publicapi")
    .WithReference(tracking)
    .WaitFor(tracking)
    .WithReference(notification)
    .WaitFor(notification)
    .WithExternalHttpEndpoints()
    .WithHealthProbes();

// Shared by ParcelGo (signs webhooks) and the Gateway (verifies them). The value lives in the AppHost's user secrets.
var parcelGoSecret = builder.AddParameter("parcelgo-webhook-secret", secret: true);

// Simulated carriers with faults you can switch on.
var carriers = builder.AddProject<Projects.CarrierHub_MockCarriers>("mockcarriers")
    .WithEnvironment("ParcelGo__WebhookSecret", parcelGoSecret)
    .WithHealthProbes();

// Carriers only ever talk to the Gateway, which forwards their events to Tracking through Service Bus.
var gateway = builder.AddProject<Projects.CarrierHub_CarrierGateway>("gateway")
    .WithReference(carriers)
    .WaitFor(carriers)
    .WithReference(tracking)
    .WaitFor(tracking)
    .WithReference(redis)
    .WaitFor(redis)
    .WithReference(messaging)
    .WaitFor(messaging)
    .WithEnvironment("ParcelGo__WebhookSecret", parcelGoSecret)
    .WithHealthProbes();

// ParcelGo needs an address to push to: the Gateway's own https endpoint.
gateway.WithEnvironment("ParcelGo__CallbackBaseUrl", gateway.GetEndpoint("https"));

// Another team's service, consumed over MCP by the Ops Assistant.
var calendarMcp = builder.AddProject<Projects.CarrierHub_CalendarMcp>("calendarmcp")
    .WithHealthProbes();

// The AI ops assistant. Its shipment data comes only through the Public API, with the caller's token; its own
// database holds what it prepared for approval and the audit trail, nothing else.
var opsAssistant = builder.AddProject<Projects.CarrierHub_OpsAssistant>("opsassistant")
    .WithReference(publicApi)
    .WaitFor(publicApi)
    .WithReference(assistantDb)
    .WaitFor(assistantDb)
    .WithReference(calendarMcp)
    .WithEnvironment("Assistant__Provider", "AzureOpenAI")            // or "Ollama" for the local model
    .WithEnvironment("Assistant__Ollama__Endpoint", "http://localhost:11434")
    .WithEnvironment("Assistant__Ollama__Model", "qwen2.5:7b")
    .WithEnvironment("Database__MigrateOnStartup", "true")
    .WithExternalHttpEndpoints()   // public in Azure: every endpoint checks the caller, A2A/AG-UI are Development-only
    .WithHealthProbes();

// The lateness sweep: the OpsAssistant image started with --sweep-once (one sweep, then exit). Locally it runs only
// when you click Start in the dashboard, because it can spend model tokens. In Azure: a scheduled Container Apps job.
var sweepJob = builder.AddProject<Projects.CarrierHub_OpsAssistant>("sweep", launchProfileName: null)
    .WithArgs("--sweep-once")
    .WithReference(publicApi)
    .WaitFor(publicApi)
    .WithReference(assistantDb)
    .WaitFor(assistantDb)
    .WithReference(calendarMcp)
    .WithEnvironment("Assistant__Provider", "AzureOpenAI")
    .WithExplicitStart()
    .PublishAsScheduledAzureContainerAppJob("0 6 * * *");   // every day at 06:00 UTC

// Without a launch profile nothing sets the environment, and outside Development user secrets (the local
// dev token and AI keys) aren't loaded.
if (!builder.ExecutionContext.IsPublishMode)
    sweepJob.WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development");

// Azure-only settings: keyless AI access for OpsAssistant, and Entra sign-in for the Public API.
// This block runs only when azd reads the AppHost (publish mode). Locally, user secrets are used.
if (builder.ExecutionContext.IsPublishMode)
{
    // OpsAssistant uses DocChat's existing AI resources with its managed identity, no keys.
    const string aiGroup = "rg-docchat-dev";

    var foundry = builder.AddAzureOpenAI("foundry").PublishAsExisting("docchat-foundry", aiGroup);
    var search = builder.AddAzureSearch("search").PublishAsExisting("docchat-search-burak", aiGroup);
    // Aspire has no Content Safety resource. An existing "OpenAI" resource is just a named
    // Cognitive Services account, which is all a role assignment needs.
    var contentSafety = builder.AddAzureOpenAI("contentsafety").PublishAsExisting("carrierhub-contentsafety", aiGroup);

    // OpsAssistant and the sweep job each use DocChat's AI resources with their OWN managed identity, no keys.
    foreach (var app in new[] { opsAssistant, sweepJob })
        app.WithRoleAssignments(foundry, CognitiveServicesBuiltInRole.CognitiveServicesOpenAIUser)
            .WithRoleAssignments(search, SearchBuiltInRole.SearchIndexDataReader)
            .WithRoleAssignments(contentSafety, CognitiveServicesBuiltInRole.CognitiveServicesUser)
            .WithEnvironment("Assistant__AzureOpenAI__Endpoint", "https://docchat-foundry.cognitiveservices.azure.com/")
            .WithEnvironment("Assistant__Search__Endpoint", "https://docchat-search-burak.search.windows.net")
            .WithEnvironment("Assistant__ContentSafety__Endpoint", "https://carrierhub-contentsafety.cognitiveservices.azure.com/")
            .WithEnvironment("Assistant__ContentSafety__Mode", "Observe");

    // Sign-in for the Public API in Azure. Not secrets, but specific to my tenant, so they're azd parameters
    // (asked once at azd up, kept in .azure/), not values in the repo.
    var entraTenantId = builder.AddParameter("entraTenantId");
    var entraPublicApiClientId = builder.AddParameter("entraPublicApiClientId");
    var entraMerchantUserObjectId = builder.AddParameter("entraMerchantUserObjectId");

    publicApi
        .WithEnvironment("Entra__TenantId", entraTenantId)
        .WithEnvironment("Entra__PublicApi__ClientId", entraPublicApiClientId)
        .WithEnvironment("Entra__MerchantUsers__0__ObjectId", entraMerchantUserObjectId)
        .WithEnvironment("Entra__MerchantUsers__0__MerchantId", "merchant-1");

    // The sweep job's own identity: it signs in to the Public API as itself (app role Shipments.Read.App, given
    // by the postprovision hook) and is mapped to merchant-1, like the user above.
    var sweepIdentity = builder.AddAzureUserAssignedIdentity("sweep-identity");
    sweepJob.WithAzureUserAssignedIdentity(sweepIdentity)
        .WithEnvironment("Assistant__Sweep__Audience", ReferenceExpression.Create($"api://{entraPublicApiClientId}"));
    publicApi
        .WithEnvironment("Entra__MerchantUsers__1__ObjectId", sweepIdentity.GetOutput("principalId"))
        .WithEnvironment("Entra__MerchantUsers__1__MerchantId", "merchant-1");

    // Telemetry history and alerts in Azure. The Aspire dashboard is for development: it only lives as long as the
    // environment, and nobody watches it. Each service gets APPLICATIONINSIGHTS_CONNECTION_STRING.
    var insights = builder.AddAzureApplicationInsights("insights");
    foreach (var service in new[] { tracking, notification, publicApi, carriers, gateway, calendarMcp, opsAssistant, sweepJob })
        service.WithReference(insights);

    // The P95 latency alert, written by hand in ops/alerts.bicep: azd creates it with everything else.
    // The email is an azd parameter (asked once, kept in .azure/), not a value in the repo.
    var alertEmail = builder.AddParameter("alertEmail");
    builder.AddBicepTemplate("alerts", "../../ops/alerts.bicep")
        .WithParameter("appInsightsName", insights.GetOutput("name"))
        .WithParameter("alertEmail", alertEmail);
}

// CarrierHub's tools as a remote MCP server (Streamable HTTP at /mcp).
// Local only for now: in Azure it needs its Entra settings (client ID, client secret for on-behalf-of,
// Mcp:ResourceUrl) and nothing there calls it yet, so azd doesn't deploy it.
builder.AddProject<Projects.CarrierHub_McpHttp>("mcphttp")
    .WithReference(publicApi)
    .WaitFor(publicApi)
    .ExcludeFromManifest();

builder.Build().Run();

static class HealthProbes
{
    // Container Apps probes for a service using ServiceDefaults: startup and liveness call /alive (the process
    // only), readiness calls /health (what the service can't work without).
    public static IResourceBuilder<ProjectResource> WithHealthProbes(this IResourceBuilder<ProjectResource> project) =>
        project.PublishAsAzureContainerApp((infra, app) =>
        {
            var container = app.Template.Containers.Single().Value!;

            // Startup: up to 2.5 min (30 × 5 s) for the process to come up, including migrations.
            container.Probes.Add(new ContainerAppProbe
            {
                ProbeType = ContainerAppProbeType.Startup,
                HttpGet = new ContainerAppHttpRequestInfo { Path = "/alive", Port = 8080 },
                PeriodSeconds = 5,
                TimeoutSeconds = 3,
                FailureThreshold = 30,
            });

            // Readiness: no traffic while a dependency is down. The 5 s timeout decides before the 15 s check ends.
            container.Probes.Add(new ContainerAppProbe
            {
                ProbeType = ContainerAppProbeType.Readiness,
                HttpGet = new ContainerAppHttpRequestInfo { Path = "/health", Port = 8080 },
                PeriodSeconds = 10,
                TimeoutSeconds = 5,
                FailureThreshold = 3,
            });

            // Liveness: restart only when the process itself stops answering.
            container.Probes.Add(new ContainerAppProbe
            {
                ProbeType = ContainerAppProbeType.Liveness,
                HttpGet = new ContainerAppHttpRequestInfo { Path = "/alive", Port = 8080 },
                PeriodSeconds = 10,
                TimeoutSeconds = 3,
                FailureThreshold = 3,
            });
        });
}
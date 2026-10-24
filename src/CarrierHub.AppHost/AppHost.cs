var builder = DistributedApplication.CreateBuilder(args);

// PostgreSQL in a container; the volume keeps data between runs.
var postgres = builder.AddPostgres("postgres").WithDataVolume();
var trackingDb = postgres.AddDatabase("trackingdb");

// Redis: the Gateway's memory of which events it already forwarded. No volume on purpose:
// a restart starts empty, so every measurement run starts from the same point.
var redis = builder.AddRedis("redis");

// Tracking owns shipments and their timeline. Only it talks to trackingdb.
var tracking = builder.AddProject<Projects.CarrierHub_Tracking>("tracking")
    .WithReference(trackingDb)
    .WaitFor(trackingDb);

// The merchant-facing API. It reaches shipment data only through Tracking.
builder.AddProject<Projects.CarrierHub_PublicApi>("publicapi")
    .WithReference(tracking)
    .WaitFor(tracking)
    .WithExternalHttpEndpoints();

// Shared by ParcelGo (signs webhooks) and the Gateway (verifies them). The value lives in the AppHost's user secrets.
var parcelGoSecret = builder.AddParameter("parcelgo-webhook-secret", secret: true);

// Simulated carriers with faults you can switch on.
var carriers = builder.AddProject<Projects.CarrierHub_MockCarriers>("mockcarriers")
    .WithEnvironment("ParcelGo__WebhookSecret", parcelGoSecret);

// Carriers only ever talk to the Gateway. Until Phase 3, the Gateway forwards to Tracking over HTTP.
var gateway = builder.AddProject<Projects.CarrierHub_CarrierGateway>("gateway")
    .WithReference(carriers)
    .WaitFor(carriers)
    .WithReference(tracking)
    .WaitFor(tracking)
    .WithReference(redis)
    .WaitFor(redis)
    .WithEnvironment("ParcelGo__WebhookSecret", parcelGoSecret);

// ParcelGo needs an address to push to: the Gateway's own https endpoint.
gateway.WithEnvironment("ParcelGo__CallbackBaseUrl", gateway.GetEndpoint("https"));

builder.Build().Run();

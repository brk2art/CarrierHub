var builder = DistributedApplication.CreateBuilder(args);

// PostgreSQL in a container; the volume keeps data between runs.
var postgres = builder.AddPostgres("postgres").WithDataVolume();
var trackingDb = postgres.AddDatabase("trackingdb");

// Tracking owns shipments and their timeline. Only it talks to trackingdb.
var tracking = builder.AddProject<Projects.CarrierHub_Tracking>("tracking")
    .WithReference(trackingDb)
    .WaitFor(trackingDb);

// The merchant-facing API. It reaches shipment data only through Tracking.
builder.AddProject<Projects.CarrierHub_PublicApi>("publicapi")
    .WithReference(tracking)
    .WaitFor(tracking)
    .WithExternalHttpEndpoints();

// Simulated carriers with faults you can switch on.
var carriers = builder.AddProject<Projects.CarrierHub_MockCarriers>("mockcarriers");

// Carriers only ever talk to the Gateway. Until Phase 3, the Gateway forwards to Tracking over HTTP.
builder.AddProject<Projects.CarrierHub_CarrierGateway>("gateway")
    .WithReference(carriers)
    .WaitFor(carriers)
    .WithReference(tracking)
    .WaitFor(tracking);

builder.Build().Run();

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
    .WithReference(trackingDb)
    .WaitFor(trackingDb)
    .WithExternalHttpEndpoints();

builder.Build().Run();

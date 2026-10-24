var builder = DistributedApplication.CreateBuilder(args);

// PostgreSQL in a container; the volume keeps data between runs.
var postgres = builder.AddPostgres("postgres").WithDataVolume();
var trackingDb = postgres.AddDatabase("trackingdb");
var notificationDb = postgres.AddDatabase("notificationdb");

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

// For the Notification service (next step).
shipmentEvents.AddServiceBusSubscription("notifications")
    .WithProperties(subscription =>
    {
        subscription.RequiresSession = true;
        subscription.MaxDeliveryCount = 10;
        subscription.DeadLetteringOnMessageExpiration = true;
        subscription.MaxDeliveryCount = 5;
    });

// Tracking owns shipments and their timeline. Only it talks to trackingdb.
var tracking = builder.AddProject<Projects.CarrierHub_Tracking>("tracking")
    .WithReference(trackingDb)
    .WaitFor(trackingDb)
    .WithReference(messaging)
    .WaitFor(messaging);

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
    .WithReference(messaging)
    .WaitFor(messaging)
    .WithEnvironment("ParcelGo__WebhookSecret", parcelGoSecret);

// ParcelGo needs an address to push to: the Gateway's own https endpoint.
gateway.WithEnvironment("ParcelGo__CallbackBaseUrl", gateway.GetEndpoint("https"));

// Delivers status changes to merchants' own webhook URLs.
builder.AddProject<Projects.CarrierHub_Notification>("notification")
    .WithReference(notificationDb)
    .WaitFor(notificationDb)
    .WithReference(messaging)
    .WaitFor(messaging);

builder.Build().Run();

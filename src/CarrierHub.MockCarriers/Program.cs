using CarrierHub.MockCarriers;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<CarrierState>();

var app = builder.Build();

app.MapGet("/", () => "CarrierHub Mock Carriers: fastship (JSON), nordpost (XML); parcelgo (webhooks) comes in step 4");
app.MapFastShip();
app.MapNordPost();
app.MapAdmin();
app.MapDefaultEndpoints();

app.Run();

public partial class Program { }   // for tests
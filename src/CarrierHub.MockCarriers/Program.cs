using CarrierHub.MockCarriers;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<CarrierState>();

builder.Services.AddSingleton<ParcelGoSubscriptions>();
builder.Services.AddHttpClient("webhooks");   // ParcelGo calls the Gateway's absolute URL, like a real carrier would
builder.Services.AddHostedService<ParcelGoWebhookSender>();

var app = builder.Build();

app.MapGet("/", () => "CarrierHub Mock Carriers: fastship (JSON), nordpost (XML); parcelgo (webhooks) comes in step 4");
app.MapFastShip();
app.MapNordPost();
app.MapParcelGo();
app.MapAdmin();
app.MapDefaultEndpoints();

app.Run();

public partial class Program { }   // for tests
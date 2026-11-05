using CarrierHub.AssistantTools;
using CarrierHub.OpsAssistant;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddSingleton(TimeProvider.System);

// The Public API, found through Aspire's service discovery. It gets the standard resilience handler from
// ServiceDefaults; the model's client doesn't, on purpose: a 10 s attempt timeout would cut answers off.
builder.Services.AddHttpClient<PublicApiClient>(c => c.BaseAddress = new Uri("https+http://publicapi"));

// Which model answers: Assistant:Provider ("Ollama" or "AzureOpenAI") comes from the AppHost; the Azure endpoint
// and key come from user secrets.
var options = builder.Configuration.GetSection("Assistant").Get<AssistantOptions>() ?? new AssistantOptions();
builder.Services.AddSingleton(options);

// Prompts and answers go into traces in Development only.
builder.Services.AddSingleton(Assistant.CreateChatClient(options, enableSensitiveData: builder.Environment.IsDevelopment()));
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<ConversationStore>();
builder.Services.AddScoped<Assistant>();

var app = builder.Build();

app.MapGet("/", () => "CarrierHub Ops Assistant");
app.MapAssistant();
app.MapDefaultEndpoints();

app.Run();

public partial class Program { }   // for tests
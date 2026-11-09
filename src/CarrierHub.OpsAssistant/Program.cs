using CarrierHub.AssistantTools;
using CarrierHub.OpsAssistant;
using CarrierHub.OpsAssistant.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddSingleton(TimeProvider.System);

// The Public API, found through Aspire's service discovery. It gets the standard resilience handler from
// ServiceDefaults; the model's client doesn't, on purpose: a 10 s attempt timeout would cut answers off.
builder.Services.AddHttpClient<PublicApiClient>(c => c.BaseAddress = new Uri("https+http://publicapi"));

// The only client that writes, used only by the approval endpoint. Its retries (from ServiceDefaults) repeat the
// POST with the same Idempotency-Key, so they can't open a second claim.
builder.Services.AddHttpClient<ClaimExecutor>(c => c.BaseAddress = new Uri("https+http://publicapi"));

// What the assistant prepared for approval, and the audit trail: in PostgreSQL, so a restart loses nothing.
builder.AddNpgsqlDbContext<AssistantDbContext>("assistantdb");   // connection string comes from Aspire
builder.Services.AddScoped<IPendingActionStore, PostgresPendingActionStore>();

// Which model answers: Assistant:Provider ("Ollama" or "AzureOpenAI") comes from the AppHost; the Azure endpoint
// and key come from user secrets.
var options = builder.Configuration.GetSection("Assistant").Get<AssistantOptions>() ?? new AssistantOptions();
builder.Services.AddSingleton(options);

// The carriers' terms: one instance for every question (no token: the terms are the same for every merchant).
if (Assistant.CreatePolicyTools(options) is { } policies)
    builder.Services.AddSingleton(policies);

// Prompts and answers go into traces in Development only.
builder.Services.AddSingleton(Assistant.CreateChatClient(options, enableSensitiveData: builder.Environment.IsDevelopment()));
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<ConversationStore>();
builder.Services.AddSingleton<McpToolSource>();
builder.Services.AddScoped<Assistant>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    // Development only: bring the database up to date at startup, as Tracking does.
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<AssistantDbContext>().Database.MigrateAsync();
}

// The chat page (src/CarrierHub.OpsAssistant.Web, built into wwwroot): index.html at /, then its files.
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapAssistant();
app.MapActions();
app.MapDefaultEndpoints();

app.MapFallbackToFile("index.html");
app.Run();

public partial class Program { }   // for tests
using CarrierHub.OpsAssistant;
using Microsoft.Extensions.AI;
using OllamaSharp;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddSingleton(TimeProvider.System);

// The Public API, found through Aspire's service discovery. It gets the standard resilience handler from
// ServiceDefaults; the model's client below doesn't, on purpose: a 10 s attempt timeout would cut answers off.
builder.Services.AddHttpClient<PublicApiClient>(c => c.BaseAddress = new Uri("https+http://publicapi"));

var endpoint = builder.Configuration["Ollama:Endpoint"] ?? "http://localhost:11434";
var model = builder.Configuration["Ollama:Model"] ?? "qwen2.5:7b";

// One pipeline for every question: the model, then automatic tool calling around it, with a trace span and
// token metrics for every model call. Prompts and answers go into traces in Development only.
builder.Services.AddChatClient(new OllamaApiClient(new Uri(endpoint), model))
    .UseFunctionInvocation(configure: f =>
    {
        f.MaximumIterationsPerRequest = 5;          // a model stuck calling tools stops here
        f.MaximumConsecutiveErrorsPerRequest = 2;
    })
    .UseOpenTelemetry(sourceName: "CarrierHub.OpsAssistant",
        configure: o => o.EnableSensitiveData = builder.Environment.IsDevelopment());

var app = builder.Build();

app.MapGet("/", () => "CarrierHub Ops Assistant");
app.MapAssistant();
app.MapDefaultEndpoints();

app.Run();

public partial class Program { }   // for tests
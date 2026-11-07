using Azure;
using Azure.AI.OpenAI;
using Azure.Search.Documents;
using CarrierHub.AssistantTools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

var builder = Host.CreateApplicationBuilder(args);

// Over stdio, standard output IS the connection to the AI client: every line written there must be an MCP message.
// A normal log line on stdout would break the protocol, so all logs go to standard error instead.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

// The AI client starts this server as a process and passes these in its environment.
// One process = one user, so the token is read once.
var apiUrl = Environment.GetEnvironmentVariable("CARRIERHUB_API_URL")
    ?? throw new InvalidOperationException("Set CARRIERHUB_API_URL to the Public API address.");
var token = Environment.GetEnvironmentVariable("CARRIERHUB_TOKEN")
    ?? throw new InvalidOperationException("Set CARRIERHUB_TOKEN to a merchant token with the shipments:read scope.");

// The same tools the Ops Assistant uses, bound to this user's token.
var api = new PublicApiClient(new HttpClient { BaseAddress = new Uri(apiUrl) });
var tools = new ShipmentTools(api, token);

List<McpServerTool> mcpTools =
[
    McpServerTool.Create(tools.CountShipments, new() { Name = "count_shipments" }),
    McpServerTool.Create(tools.FindShipments, new() { Name = "find_shipments" }),
    McpServerTool.Create(tools.FindShipmentsNeedingAttention, new() { Name = "find_shipments_needing_attention" }),
    McpServerTool.Create(tools.GetShipmentTimeline, new() { Name = "get_shipment_timeline" }),
];

// The carriers' terms, if these are set too: a search query key (it can only search) and Azure OpenAI for the
// embeddings, which must come from the same model the index was built with.
var searchEndpoint = Environment.GetEnvironmentVariable("CARRIERHUB_SEARCH_ENDPOINT");
var searchKey = Environment.GetEnvironmentVariable("CARRIERHUB_SEARCH_QUERY_KEY");
var openAiEndpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
var openAiKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");

if (searchEndpoint is not null && searchKey is not null && openAiEndpoint is not null && openAiKey is not null)
{
    var search = new SearchClient(new Uri(searchEndpoint), PolicyTools.IndexName, new AzureKeyCredential(searchKey));
    var embedder = new AzureOpenAIClient(new Uri(openAiEndpoint), new AzureKeyCredential(openAiKey))
        .GetEmbeddingClient(Environment.GetEnvironmentVariable("AZURE_OPENAI_EMBEDDING_DEPLOYMENT") ?? "embedding")
        .AsIEmbeddingGenerator();

    var policies = new PolicyTools(search, embedder);
    mcpTools.Add(McpServerTool.Create(policies.SearchCarrierPolicies, new() { Name = "search_carrier_policies" }));
}

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools(mcpTools);

await builder.Build().RunAsync();
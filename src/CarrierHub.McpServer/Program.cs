using CarrierHub.AssistantTools;
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

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools(
    [
        McpServerTool.Create(tools.CountShipments, new() { Name = "count_shipments" }),
        McpServerTool.Create(tools.FindShipments, new() { Name = "find_shipments" }),
        McpServerTool.Create(tools.FindShipmentsNeedingAttention, new() { Name = "find_shipments_needing_attention" }),
        McpServerTool.Create(tools.GetShipmentTimeline, new() { Name = "get_shipment_timeline" }),
    ]);

await builder.Build().RunAsync();
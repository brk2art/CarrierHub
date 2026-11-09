using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace CarrierHub.OpsAssistant;

// Tools from a server CarrierHub doesn't own (the Calendar team's MCP server), found at runtime.
// Three rules for tools from outside:
//   1. only the names on our allowlist are used, whatever else the server offers (it could add "delete_everything");
//   2. their results are data, never instructions (the system prompt already says so for every tool);
//   3. if the server is down, the assistant answers without them and tries again later. It never fails because of it.
public sealed class McpToolSource(IConfiguration config, ILogger<McpToolSource> logger, TimeProvider clock) : IAsyncDisposable
{
    private static readonly HashSet<string> Allowed = ["get_public_holidays", "add_working_days"];
    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private McpClient? _client;
    private IReadOnlyList<AITool> _tools = [];
    private DateTimeOffset _nextAttempt = DateTimeOffset.MinValue;

    // Connects on first use, so the assistant starts even when the calendar is down.
    public async Task<IReadOnlyList<AITool>> GetToolsAsync(CancellationToken ct)
    {
        if (_client is not null || clock.GetUtcNow() < _nextAttempt)
            return _tools;

        await _gate.WaitAsync();
        try
        {
            if (_client is not null || clock.GetUtcNow() < _nextAttempt)
                return _tools;

            if (Endpoint() is not { } endpoint)
            {
                _nextAttempt = DateTimeOffset.MaxValue;   // not configured: never try
                return _tools;
            }

            try
            {
                var client = await McpClient.CreateAsync(
                    new HttpClientTransport(new HttpClientTransportOptions { Name = "calendar", Endpoint = endpoint }),
                    cancellationToken: ct);

                // tools/list: what the server offers right now. Each McpClientTool IS an AIFunction: the agent calls it
                // like its own tools, and the call goes to the server over MCP.
                var offered = await client.ListToolsAsync(cancellationToken: ct);
                foreach (var ignored in offered.Where(t => !Allowed.Contains(t.Name)))
                    logger.LogWarning("Calendar MCP server offers {Tool}, which is not on the allowlist: ignored", ignored.Name);

                _tools = offered.Where(t => Allowed.Contains(t.Name)).Cast<AITool>().ToList();
                _client = client;
                logger.LogInformation("Calendar MCP server at {Endpoint}: using {Tools}", endpoint,
                    string.Join(", ", _tools.Select(t => t.Name)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _nextAttempt = clock.GetUtcNow() + RetryAfter;
                logger.LogWarning(ex, "Calendar MCP server unavailable; answering without its tools, retrying in {Seconds} s",
                    RetryAfter.TotalSeconds);
            }

            return _tools;
        }
        finally
        {
            _gate.Release();
        }
    }

    // A configured URL, or the address Aspire gives the "calendarmcp" resource.
    private Uri? Endpoint() =>
        config["Assistant:Calendar:Url"] is { } url ? new Uri(url)
        : config["services:calendarmcp:http:0"] is { } baseUrl ? new Uri(new Uri(baseUrl), "/mcp")
        : null;

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
            await _client.DisposeAsync();
    }
}

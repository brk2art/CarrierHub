using Azure;
using Azure.AI.OpenAI;
using Azure.Search.Documents;
using CarrierHub.AssistantTools;
using CarrierHub.McpHttp;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.AI;
using Microsoft.Identity.Client;
using ModelContextProtocol;
using ModelContextProtocol.AspNetCore.Authentication;
using ModelContextProtocol.Server;

// CarrierHub's tools as a remote MCP server (Streamable HTTP at /mcp), protected with Microsoft Entra ID:
//   - only Entra tokens issued FOR THIS SERVER, with the Mcp.Tools scope, are accepted;
//   - without one: 401, and the response says where to sign in (protected resource metadata);
//   - every call reaches the Public API with a NEW token for the same user (on-behalf-of), never the one received.

const string McpScope = "Mcp.Tools";
const string PublicApiTokenKey = "CarrierHub.PublicApiToken";

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddHttpContextAccessor();  // a tool reads the current request's Authorization header through this
// The Public API through Aspire's service discovery, as in the Ops Assistant.
builder.Services.AddHttpClient<PublicApiClient>(c => c.BaseAddress = new Uri("https+http://publicapi"));

string Required(string key) => builder.Configuration[key]
    ?? throw new InvalidOperationException($"Set {key} in user secrets: this server only accepts Entra ID tokens.");

var tenantId = Required("Entra:TenantId");
var mcpClientId = Required("Entra:Mcp:ClientId");
var publicApiClientId = Required("Entra:PublicApi:ClientId");
var resourceUrl = Required("Mcp:ResourceUrl");
var authority = $"https://login.microsoftonline.com/{tenantId}/v2.0";

builder.Services.AddAuthentication(o =>
    {
        // Valid tokens are checked by JWT bearer; a missing or bad one gets the MCP challenge: 401 with a
        // WWW-Authenticate header that points to this server's metadata, so a client can find where to sign in.
        o.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        o.DefaultChallengeScheme = McpAuthenticationDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(o =>
    {
        o.Authority = authority;    // Entra's issuer and signing keys
        o.TokenValidationParameters.ValidAudiences = [mcpClientId, $"api://{mcpClientId}"];   // tokens FOR ME only]
        o.MapInboundClaims = false; // keep "scp" and "oid" as Entra names them
    })
    .AddMcp(o =>
    {
        // Served at /.well-known/oauth-protected-resource…: "I am this resource; sign in there, with this scope."
        o.ResourceMetadata = new()
        {
            Resource = resourceUrl,
            AuthorizationServers = { authority },
            ScopesSupported = [$"api://{mcpClientId}/{McpScope}"]
        };
    });

// A valid token is not enough: it must carry the scope the user consented to for this server.
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("mcp-tools", p => p
        .RequireAuthenticatedUser()
        .RequireAssertion(c => (c.User.FindFirst("scp")?.Value ?? "").Split(' ').Contains(McpScope)));


// This server's own identity towards Entra (the client secret), used only for on-behalf-of.
builder.Services.AddSingleton(new PublicApiTokens(
    ConfidentialClientApplicationBuilder.Create(mcpClientId)
        .WithClientSecret(Required("Entra:Mcp:ClientSecret"))
        .WithAuthority($"https://login.microsoftonline.com/{tenantId}")
        .Build(),
    $"api://{publicApiClientId}/Shipments.Read"));

List<McpServerTool> tools =
[
    ShipmentTool(nameof(ShipmentTools.CountShipments), "count_shipments"),
    ShipmentTool(nameof(ShipmentTools.FindShipments), "find_shipments"),
    ShipmentTool(nameof(ShipmentTools.FindShipmentsNeedingAttention), "find_shipments_needing_attention"),
    ShipmentTool(nameof(ShipmentTools.GetShipmentTimeline), "get_shipment_timeline"),
];

// The carriers' terms need no merchant token: one instance for every call.
if (CreatePolicyTools(builder.Configuration) is { } policies)
    tools.Add(McpServerTool.Create(policies.SearchCarrierPolicies, new() { Name = "search_carrier_policies" }));

builder.Services
    .AddMcpServer()
    // Stateless: each call is a plain HTTP request, no session kept on the server. Simple to scale, and enough for
    // tools (it rules out server-to-client requests such as sampling, which we don't use).
    .WithHttpTransport(o => o.Stateless = true)
    .WithTools(tools);

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

// For an accepted call to /mcp: swap the user's token for a Public API token, before any tool runs. Async work like
// this can't happen inside a tool's constructor, so it happens here and the tool picks up the result.
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/mcp") && context.User.Identity?.IsAuthenticated == true)
    {
        var userToken = context.Request.Headers.Authorization.ToString()["Bearer ".Length..].Trim();
        try
        {
            context.Items[PublicApiTokenKey] = await context.RequestServices
                .GetRequiredService<PublicApiTokens>().GetAsync(userToken, context.RequestAborted);
        }
        catch (MsalException ex)
        {
            app.Logger.LogWarning(ex, "On-behalf-of failed");   // the details stay in the log
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "This user can't get access to the Public API." });
            return;
        }
    }
    await next(context);
});

app.MapMcp("/mcp").RequireAuthorization("mcp-tools");
app.MapDefaultEndpoints();

app.Run();

// A shipment tool whose ShipmentTools object is created on EVERY call, with that request's token: the same idea as
// the Ops Assistant building its tools per question. The [Description] texts on ShipmentTools' methods are kept,
// so MCP clients read exactly what the Ops Assistant's model reads.
static McpServerTool ShipmentTool(string method, string name) =>
    McpServerTool.Create(
        typeof(ShipmentTools).GetMethod(method)!,
        context =>
        {
            var services = context.Services ?? throw new InvalidOperationException("No request services.");
            var http = services.GetRequiredService<IHttpContextAccessor>().HttpContext;
            if (http?.Items[PublicApiTokenKey] is not string publicApiToken)
                throw new McpException("Not signed in");
            return new ShipmentTools(services.GetRequiredService<PublicApiClient>(), publicApiToken);
        },
        new() { Name = name });

// The same settings as the Ops Assistant (shared user secrets). Null when search isn't configured.
static PolicyTools? CreatePolicyTools(IConfiguration config)
{
    var searchEndpoint = config["Assistant:Search:Endpoint"];
    var searchKey = config["Assistant:Search:QueryKey"];
    var openAiEndpoint = config["Assistant:AzureOpenAI:Endpoint"];
    var openAiKey = config["Assistant:AzureOpenAI:ApiKey"];
    if (searchEndpoint is null || searchKey is null || openAiEndpoint is null || openAiKey is null)
        return null;

    var search = new SearchClient(new Uri(searchEndpoint), PolicyTools.IndexName, new AzureKeyCredential(searchKey));
    var embedder = new AzureOpenAIClient(new Uri(openAiEndpoint), new AzureKeyCredential(openAiKey))
        .GetEmbeddingClient(config["Assistant:AzureOpenAI:EmbeddingDeployment"] ?? "embedding")
        .AsIEmbeddingGenerator();
    return new PolicyTools(search, embedder);
}
using System.Net.ServerSentEvents;
using CarrierHub.AssistantTools;

namespace CarrierHub.OpsAssistant;

public static class AssistantEndpoints
{
    public static void MapAssistant(this WebApplication app)
    {
        // Every AI endpoint checks the caller first, so a bad token never reaches the model (or its bill).
        // A filter on the group, not a check per handler: a new endpoint can't forget it.
        var assistant = app.MapGroup("/v1/assistant").AddEndpointFilter(RequireValidTokenAsync);
        assistant.MapPost("/ask", AskAsync);
        assistant.MapPost("/ask/stream", AskStreamAsync);
        assistant.MapPost("/lateness", CheckLatenessAsync);
    }

    // Runs before each handler: one quick /v1/me call (~20 ms) instead of model calls with a token nobody checked.
    private static async ValueTask<object?> RequireValidTokenAsync(EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (Token(http.Request) is not { } token)
            return Results.Unauthorized();

        var api = http.RequestServices.GetRequiredService<PublicApiClient>();
        var me = await api.MeAsync(token, http.RequestAborted);
        if (me.StatusCode is 401 or 403)
            return Results.StatusCode(me.StatusCode);
        if (me.StatusCode != 200)
            return Results.Problem("Could not check the token. Try again shortly.",
                statusCode: StatusCodes.Status502BadGateway);

        return await next(context);
    }

    private static async Task<IResult> AskAsync(AskRequest request, HttpRequest http, Assistant assistant, CancellationToken ct)
    {
        if (Invalid(request) is { } invalid)
            return invalid;
        if (Token(http) is not { } token)
            return Results.Unauthorized();

        var result = await assistant.AskAsync(request.Question, token, request.ConversationId, ct);

        if (result.UnknownConversation)
            return UnknownConversation();

        return result.RejectedWith is { } status ? Results.StatusCode(status) : Results.Ok(result.Response);
    }

    // Server-Sent Events: tool calls as they happen, the answer as it is written, then the full response.
    // Everything that can fail is checked BEFORE the first byte: once the stream starts, the status is already 200.
    private static async Task<IResult> AskStreamAsync(AskRequest request, HttpRequest http, Assistant assistant,
        PublicApiClient api, IConversationStore conversations, CancellationToken ct)
    {
        if (Invalid(request) is { } invalid)
            return invalid;
        if (Token(http) is not { } token)
            return Results.Unauthorized();

        // One quick check of the token, so a bad one gets a real 401 or 403 instead of an error inside a 200 stream.
        var me = await api.MeAsync(token, ct);
        if (me.StatusCode is 401 or 403)
            return Results.StatusCode(me.StatusCode);
        if (me.StatusCode != 200)
            return Results.Problem("Could not check the token. Try again shortly.", statusCode: StatusCodes.Status502BadGateway);

        if (request.ConversationId is { } id && await conversations.LoadAsync(id, token, ct) is null)
            return UnknownConversation();

        return TypedResults.ServerSentEvents(Events(assistant.AskStreamingAsync(request.Question, token,
            request.ConversationId, ct)));
    }

    // Structured output: a typed verdict for code to use, not a sentence.
    private static async Task<IResult> CheckLatenessAsync(AskRequest request, HttpRequest http, Assistant assistant,
        CancellationToken ct)
    {
        if (Invalid(request) is { } invalid)
            return invalid;
        if (Token(http) is not { } token)
            return Results.Unauthorized();

        var result = await assistant.CheckLatenessAsync(request.Question, token, ct);
        return result.RejectedWith is { } status ? Results.StatusCode(status) : Results.Ok(result.Verdict);
    }

    // Each event becomes "event: <type>" + "data: <json>".
    private static async IAsyncEnumerable<SseItem<object>> Events(IAsyncEnumerable<Assistant.AssistantEvent> events)
    {
        await foreach (var e in events)
            yield return new SseItem<object>(e.Data, e.Type);
    }

    private static IResult? Invalid(AskRequest request) =>
        string.IsNullOrWhiteSpace(request.Question) || request.Question.Length > 1000
            ? Results.BadRequest(new { error = "Ask a question of 1 to 1000 characters." })
            : null;

    // No token, no assistant. The token is passed on to the Public API, which checks it and decides what this
    // merchant may see; a read-only token makes the assistant read-only.
    private static string? Token(HttpRequest http)
    {
        var authorization = http.Headers.Authorization.ToString();
        return authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authorization["Bearer ".Length..].Trim()
            : null;
    }

    private static IResult UnknownConversation() =>
        Results.NotFound(new { error = "Unknown or expired conversation. Ask again without a conversationId to start a new one." });
}
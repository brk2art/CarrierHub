using System.Net.ServerSentEvents;
using CarrierHub.AssistantTools;

namespace CarrierHub.OpsAssistant;

public static class AssistantEndpoints
{
    public static void MapAssistant(this WebApplication app)
    {
        app.MapPost("/v1/assistant/ask", AskAsync);
        app.MapPost("/v1/assistant/ask/stream", AskStreamAsync);
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
        PublicApiClient api, ConversationStore conversations, CancellationToken ct)
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

        if (request.ConversationId is { } id && conversations.Load(id, token) is null)
            return UnknownConversation();

        return TypedResults.ServerSentEvents(Events(assistant.AskStreamingAsync(request.Question, token,
            request.ConversationId, ct)));
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
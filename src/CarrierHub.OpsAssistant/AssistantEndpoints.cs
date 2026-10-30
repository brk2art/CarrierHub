namespace CarrierHub.OpsAssistant;

public static class AssistantEndpoints
{
    public static void MapAssistant(this WebApplication app) =>
        app.MapPost("/v1/assistant/ask", AskAsync);

    private static async Task<IResult> AskAsync(AskRequest request, HttpRequest http, Assistant assistant, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Question) || request.Question.Length > 1000)
            return Results.BadRequest(new { error = "Ask a question of 1 to 1000 characters." });

        // No token, no assistant. The token is passed on to the Public API, which checks it and decides what
        // this merchant may see; a read-only token makes the assistant read-only.
        var authorization = http.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return Results.Unauthorized();
        var token = authorization["Bearer ".Length..].Trim();

        var result = await assistant.AskAsync(request.Question, token, ct);
        return result.RejectedWith is { } status ? Results.StatusCode(status) : Results.Ok(result.Response);
    }
}
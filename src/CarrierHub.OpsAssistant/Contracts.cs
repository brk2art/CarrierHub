namespace CarrierHub.OpsAssistant;

public sealed record AskRequest(string Question, string? ConversationId = null);

public sealed record AskResponse(string Answer, IReadOnlyList<ToolCall> ToolCalls, string Model,
    long? InputTokens, long? OutputTokens, int DurationMs, string? ConversationId = null,
    IReadOnlyList<ProposedAction>? PendingActions = null);

public sealed record ToolCall(string Name, IDictionary<string, object?>? Arguments);

// What this answer prepared for approval. A page shows each with an Approve and a Reject button (Step 3c).
public sealed record ProposedAction(Guid Id, string Kind, string Summary, DateTimeOffset ExpiresAt);
namespace CarrierHub.OpsAssistant;

// No ConversationId: start a new conversation. With one: continue it.
public sealed record AskRequest(string Question, string? ConversationId = null);

// What the assistant did, next to what it said: the evaluations check the tool calls, a person reads the answer.
// ConversationId: send it with the next question to continue the conversation.
public sealed record AskResponse(string Answer, IReadOnlyList<ToolCall> ToolCalls, string Model,
    long? InputTokens, long? OutputTokens, int DurationMs, string? ConversationId = null);

public sealed record ToolCall(string Name, IDictionary<string, object?>? Arguments);

namespace CarrierHub.OpsAssistant;

public sealed record AskRequest(string Question);

// What the assistant did, next to what it said: the evaluations check the tool calls, a person reads the answer.
public sealed record AskResponse(string Answer, IReadOnlyList<ToolCall> ToolCalls, string Model,
    long? InputTokens, long? OutputTokens, int DurationMs);

public sealed record ToolCall(string Name, IDictionary<string, object?>? Arguments);

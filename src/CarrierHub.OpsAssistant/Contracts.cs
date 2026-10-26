namespace CarrierHub.OpsAssistant;

// The Public API's v1 shipment, as this service reads it. Our own copy: the assistant is a client of the
// public contract, like any merchant, and never sees Tracking's internal fields.
public sealed record TimelineEntry(string Status, DateTimeOffset OccurredAt, string? Location, string? Description);

public sealed record Shipment(Guid Id, string MerchantReference, string Carrier, string TrackingNumber, string Status,
    DateTimeOffset PromisedBy, DateTimeOffset? DeliveredAt, bool IsLate, IReadOnlyList<TimelineEntry> Timeline);

public sealed record AskRequest(string Question);

// What the assistant did, next to what it said: the evaluations check the tool calls, a person reads the answer.
public sealed record AskResponse(string Answer, IReadOnlyList<ToolCall> ToolCalls, string Model,
    long? InputTokens, long? OutputTokens, int DurationMs);

public sealed record ToolCall(string Name, IDictionary<string, object?>? Arguments);

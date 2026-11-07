namespace CarrierHub.OpsAssistant;

// What both assistants offer: one question in, one result out. The evaluations run either through this.
public interface IAssistant
{
    Task<AssistantResult> AskAsync(string question, string token, string? conversationId, CancellationToken ct);
}
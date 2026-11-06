using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CarrierHub.AssistantTools;
using Polly;

namespace CarrierHub.OpsAssistant;

public enum ExecutionKind
{
    Opened,       // 201, or 200: already opened with this key earlier
    Refused,      // CarrierHub said no (409, 422, 400, 404): final, approving again changes nothing
    NotAllowed,   // 401 or 403: this token can't do it; another person may
    Unknown,      // no answer, or the Public API failed: the claim may or may not exist; approving again is safe
}

public sealed record ExecutionOutcome(ExecutionKind Kind, Guid? ClaimId = null, bool Replayed = false,
    int? StatusCode = null, string? Error = null);

// The assistant's ONLY way to write, used only by the approval endpoint, never by a tool. It sends exactly what was
// stored at proposal time, with the approver's token and the stored idempotency key.
public sealed class ClaimExecutor(HttpClient http, ILogger<ClaimExecutor> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<ExecutionOutcome> OpenClaimAsync(PendingAction action, string approverToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/claims")
        {
            Content = JsonContent.Create(new { shipmentId = action.ShipmentId, reason = action.Reason, note = action.Note }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", approverToken);
        request.Headers.Add("Idempotency-Key", action.IdempotencyKey);

        try
        {
            using var response = await http.SendAsync(request, ct);
            var status = (int)response.StatusCode;

            if (status is 200 or 201)
            {
                var claim = await response.Content.ReadFromJsonAsync<ShipmentClaim>(Json, ct);
                return new(ExecutionKind.Opened, claim!.Id, Replayed: status == 200, StatusCode: status);
            }

            if (status is 401 or 403)
                return new(ExecutionKind.NotAllowed, StatusCode: status, Error: "This token may not open claims.");

            if (status is 400 or 404 or 409 or 422)
                return new(ExecutionKind.Refused, StatusCode: status, Error: await ErrorAsync(response, ct));

            return new(ExecutionKind.Unknown, StatusCode: status, Error: $"The Public API answered {status}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or ExecutionRejectedException or OperationCanceledException)
        {
            // Unreachable, timed out, or the circuit breaker is open. The request may have arrived anyway, which is
            // exactly why the key exists: the next attempt either opens the claim or finds the one already opened.
            logger.LogWarning(ex, "Opening the claim for action {ActionId} got no answer", action.Id);
            return new(ExecutionKind.Unknown, Error: "No answer from the Public API.");
        }
    }

    // The reason CarrierHub gave: "error" from our own endpoints, "detail" or "title" from a problem response.
    private static async Task<string> ErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
                foreach (var name in new[] { "error", "detail", "title" })
                    if (document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                        return value.GetString()!;
        }
        catch (JsonException)
        {
        }

        return $"The Public API refused the claim ({(int)response.StatusCode}).";
    }
}
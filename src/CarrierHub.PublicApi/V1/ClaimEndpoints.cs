using Microsoft.AspNetCore.Mvc;
using System.Net;
using System.Security.Claims;
using System.Text.Json;

namespace CarrierHub.PublicApi.V1;

public static class ClaimEndpoints
{
    public const string IdempotencyKeyHeader = "Idempotency-Key";
    private const int MaxKeyLength = 100;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapClaimEndpoints(this WebApplication app)
    {
        // The same filter and rate limit as the shipments group.
        var claims = app.MapGroup("/v1/claims")
            .AddEndpointFilter(TrackingUnavailable.Filter)
            .RequireRateLimiting(RateLimits.PerMerchant);

        claims.MapPost("/", OpenAsync).RequireAuthorization(Policies.Write);
        claims.MapGet("/{id:guid}", GetAsync).RequireAuthorization(Policies.Read);
        claims.MapGet("/", ListAsync).RequireAuthorization(Policies.Read);

        // Who am I? The Ops Assistant asks this so it never has to read an unvalidated token itself.
        app.MapGet("/v1/me", GetMe)
            .RequireAuthorization(Policies.Read)
            .RequireRateLimiting(RateLimits.PerMerchant);
    }

    private static async Task<IResult> OpenAsync(OpenClaimRequest request,
        [FromHeader(Name = IdempotencyKeyHeader)] string? idempotencyKey,
        ClaimsPrincipal user, TrackingClient tracking, CancellationToken ct)
    {
        if (!IsValidKey(idempotencyKey))
            return Results.BadRequest(new
            {
                error = $"The {IdempotencyKeyHeader} header is required: a new value (for example a GUID) for each claim, " +
                        $"repeated unchanged on a retry. At most {MaxKeyLength} visible ASCII characters.",
            });

        using var response = await tracking.OpenClaimAsync(MerchantId(user), request, idempotencyKey!, ct);

        if (response.StatusCode == HttpStatusCode.Created)
        {
            var claim = await ReadAsync<ShipmentClaim>(response, ct);
            return Results.Created($"/v1/claims/{claim.Id}", claim);   // our URL, not Tracking's
        }

        return await ForwardAsync<ShipmentClaim>(response, ct);   // 200 on a retry, or an error
    }

    private static async Task<IResult> GetAsync(Guid id, ClaimsPrincipal user, TrackingClient tracking,
        CancellationToken ct)
    {
        using var response = await tracking.GetClaimAsync(MerchantId(user), id, ct);
        return await ForwardAsync<ShipmentClaim>(response, ct);
    }

    // Newest first; optionally one shipment's claims. limit 1 to 100, default 50.
    private static async Task<IResult> ListAsync(Guid? shipmentId, int? limit, ClaimsPrincipal user,
        TrackingClient tracking, CancellationToken ct)
    {
        using var response = await tracking.ListClaimsAsync(MerchantId(user), shipmentId, limit, ct);
        return await ForwardAsync<List<ShipmentClaim>>(response, ct);
    }

    // Everything here comes from the validated token: the JWT handler checked its signature, issuer and expiry.
    private static IResult GetMe(ClaimsPrincipal user) =>
        Results.Ok(new Me(
            MerchantId(user),
            user.FindAll("scope")
                .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Distinct()
                .Order()
                .ToList()));

    private static async Task<IResult> ForwardAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        switch (response.StatusCode)
        {
            case HttpStatusCode.OK:
                return Results.Ok(await ReadAsync<T>(response, ct));

            case HttpStatusCode.NotFound or HttpStatusCode.BadRequest
                or HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity:
                // The caller's mistake: pass Tracking's explanation on ("Shipment not found.", "already has a Late claim").
                var body = await response.Content.ReadAsStringAsync(ct);
                return string.IsNullOrWhiteSpace(body)
                    ? Results.StatusCode((int)response.StatusCode)
                    : Results.Content(body, "application/json", statusCode: (int)response.StatusCode);

            default:
                return Results.Problem("The tracking service could not complete the request.",
                    statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct) =>
        (await response.Content.ReadFromJsonAsync<T>(Json, ct))!;

    // Visible ASCII only: a key is compared character for character, so spaces and look-alikes cause silent misses.
    private static bool IsValidKey(string? key) =>
        !string.IsNullOrEmpty(key) && key.Length <= MaxKeyLength && key.All(ch => ch is > ' ' and <= '~');

    // The authorization policy guarantees the claim is there.
    private static string MerchantId(ClaimsPrincipal user) => user.FindFirstValue(Claims.MerchantId)!;
}
using System.Net;
using System.Security.Claims;
using System.Text.Json;

namespace CarrierHub.PublicApi.V1;

public static class ShipmentEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapShipmentEndpoints(this WebApplication app)
    {
        var v1 = app.MapGroup("/v1/shipments");
        v1.MapPost("/", CreateAsync).RequireAuthorization(Policies.Write);
        v1.MapGet("/{id:guid}", GetAsync).RequireAuthorization(Policies.Read);
        v1.MapGet("/", SearchAsync).RequireAuthorization(Policies.Read);
    }

    private static async Task<IResult> CreateAsync(CreateShipmentRequest request, ClaimsPrincipal user,
        TrackingClient tracking, CancellationToken ct)
    {
        using var response = await tracking.CreateAsync(MerchantId(user), request, ct);

        if (response.StatusCode == HttpStatusCode.Created)
        {
            var shipment = await ReadAsync<Shipment>(response, ct);
            return Results.Created($"/v1/shipments/{shipment.Id}", shipment);   // our URL, not Tracking's
        }

        return await ForwardAsync<Shipment>(response, ct);
    }

    private static async Task<IResult> GetAsync(Guid id, ClaimsPrincipal user, TrackingClient tracking, CancellationToken ct)
    {
        using var response = await tracking.GetAsync(MerchantId(user), id, ct);
        return await ForwardAsync<Shipment>(response, ct);
    }

    private static async Task<IResult> SearchAsync(string? status, bool? late, string? reference, ClaimsPrincipal user,
        TrackingClient tracking, CancellationToken ct)
    {
        using var response = await tracking.SearchAsync(MerchantId(user), status, late, reference, ct);
        return await ForwardAsync<List<Shipment>>(response, ct);
    }

    // The authorization policy guarantees the claim is there.
    private static string MerchantId(ClaimsPrincipal user) => user.FindFirstValue(Claims.MerchantId)!;

    private static async Task<IResult> ForwardAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        switch (response.StatusCode)
        {
            case HttpStatusCode.OK:
                return Results.Ok(await ReadAsync<T>(response, ct));

            case HttpStatusCode.NotFound:
                return Results.NotFound();

            case HttpStatusCode.BadRequest or HttpStatusCode.Conflict:
                // The caller's mistake: pass Tracking's explanation on.
                return Results.Content(await response.Content.ReadAsStringAsync(ct), "application/json",
                    statusCode: (int)response.StatusCode);

            default:
                // Our problem, not the caller's: say so without leaking internal details.
                return Results.Problem("The tracking service could not complete the request.",
                    statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct) =>
        (await response.Content.ReadFromJsonAsync<T>(Json, ct))!;
}
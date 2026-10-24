using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Polly;

namespace CarrierHub.PublicApi.V1;

// A merchant manages its own webhook. As everywhere in the Public API, the merchant comes from the token.
public static class WebhookEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapWebhookEndpoints(this WebApplication app)
    {
        var v1 = app.MapGroup("/v1/webhook")
            .AddEndpointFilter(NotificationUnavailableFilter)
            .RequireRateLimiting(RateLimits.PerMerchant);

        v1.MapPut("/", RegisterAsync).RequireAuthorization(Policies.Write);
        v1.MapGet("/", GetAsync).RequireAuthorization(Policies.Read);
        v1.MapDelete("/", DeleteAsync).RequireAuthorization(Policies.Write);
        v1.MapGet("/deliveries", DeliveriesAsync).RequireAuthorization(Policies.Read);
    }

    private static async Task<IResult> RegisterAsync(RegisterWebhookRequest request, ClaimsPrincipal user,
        NotificationClient notification, CancellationToken ct)
    {
        using var response = await notification.RegisterAsync(MerchantId(user), request, ct);

        return response.StatusCode switch
        {
            HttpStatusCode.Created => Results.Created("/v1/webhook", await ReadAsync<Webhook>(response, ct)),   // includes the secret, once
            HttpStatusCode.OK => Results.Ok(await ReadAsync<Webhook>(response, ct)),
            HttpStatusCode.BadRequest => Results.Content(await response.Content.ReadAsStringAsync(ct), "application/json",
                statusCode: StatusCodes.Status400BadRequest),
            _ => Unavailable(),
        };
    }

    private static async Task<IResult> GetAsync(ClaimsPrincipal user, NotificationClient notification, CancellationToken ct)
    {
        using var response = await notification.GetAsync(MerchantId(user), ct);

        return response.StatusCode switch
        {
            HttpStatusCode.OK => Results.Ok(await ReadAsync<Webhook>(response, ct)),
            HttpStatusCode.NotFound => Results.NotFound(),
            _ => Unavailable(),
        };
    }

    private static async Task<IResult> DeleteAsync(ClaimsPrincipal user, NotificationClient notification, CancellationToken ct)
    {
        using var response = await notification.DeleteAsync(MerchantId(user), ct);
        return response.StatusCode == HttpStatusCode.NoContent ? Results.NoContent() : Unavailable();
    }

    private static async Task<IResult> DeliveriesAsync(ClaimsPrincipal user, NotificationClient notification, CancellationToken ct)
    {
        using var response = await notification.DeliveriesAsync(MerchantId(user), ct);
        return response.StatusCode == HttpStatusCode.OK
            ? Results.Ok(await ReadAsync<List<WebhookDelivery>>(response, ct))
            : Unavailable();
    }

    private static string MerchantId(ClaimsPrincipal user) => user.FindFirstValue(Claims.MerchantId)!;

    private static IResult Unavailable() =>
        Results.Problem("The notification service could not complete the request.", statusCode: StatusCodes.Status502BadGateway);

    private static async ValueTask<object?> NotificationUnavailableFilter(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (Exception ex) when (ex is HttpRequestException or ExecutionRejectedException)
        {
            context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("CarrierHub.PublicApi.Notification")
                .LogWarning(ex, "Call to Notification failed");

            return Results.Problem("The notification service is not available. Try again shortly.",
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct) =>
        (await response.Content.ReadFromJsonAsync<T>(Json, ct))!;
}
using Polly;

namespace CarrierHub.PublicApi.V1;

// Shared by every endpoint group that calls Tracking (shipments, claims), so they fail the same way.
internal static class TrackingUnavailable
{
    // Tracking unreachable, timed out, or its circuit breaker open (Polly's ExecutionRejectedException covers
    // the last two): our problem, not the caller's. Answer 502, log the details, and don't send them to the caller.
    public static async ValueTask<object?> Filter(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (Exception ex) when (ex is HttpRequestException or ExecutionRejectedException)
        {
            context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("CarrierHub.PublicApi.Tracking")
                .LogWarning(ex, "Call to Tracking failed");

            return Results.Problem("The tracking service is not available. Try again shortly.",
                statusCode: StatusCodes.Status502BadGateway);
        }
    }
}
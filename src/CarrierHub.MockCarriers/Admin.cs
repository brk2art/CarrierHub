namespace CarrierHub.MockCarriers;

// Switches faults on and off while the system runs. A local test tool: no authentication.
public static class Admin
{
    public static void MapAdmin(this WebApplication app)
    {
        var admin = app.MapGroup("/admin");

        admin.MapGet("/faults", (CarrierState state) => state.AllFaults());

        admin.MapPut("/faults/{carrier}", (string carrier, Faults faults, CarrierState state) =>
        {
            if (!Carriers.Known.Contains(carrier))
                return Results.NotFound(new { error = $"Unknown carrier '{carrier}'. Known: {string.Join(", ", Carriers.Known)}" });

            state.SetFaults(carrier, faults);
            return Results.Ok(faults);
        });

        // Clears faults and subscriptions, and restarts every journey.
        admin.MapPost("/reset", (CarrierState state, ParcelGoSubscriptions subscriptions, MerchantInbox inbox) =>
        {
            state.Reset();
            subscriptions.Clear();
            inbox.Clear();
            return Results.NoContent();
        });
    }
}
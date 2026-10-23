using CarrierHub.CarrierGateway;
using CarrierHub.CarrierGateway.Adapters;

namespace CarrierHub.CarrierGateway.Tests;

public class FastShipContractTests
{
    // Captured from the mock carrier on 23 October 2026, Danish summer time (+02:00).
    private const string Captured = """
        {"trackingNumber":"FS300001","events":[
          {"id":"FS-FS300001-0","code":"PU","timestamp":"2026-10-23T19:25:47.3100347+02:00","location":"Aarhus terminal"},
          {"id":"FS-FS300001-3","code":"NA","timestamp":"2026-10-23T19:27:27.3100347+02:00","location":"Customer address"}]}
        """;

    [Fact]
    public void Codes_become_statuses_and_times_become_utc()
    {
        var result = FastShipAdapter.Parse(Captured);

        Assert.Equal(new[] { ShipmentStatus.PickedUp, ShipmentStatus.DeliveryFailed }, result.Events.Select(e => e.Status));
        Assert.Equal("FS-FS300001-0", result.Events[0].CarrierEventId);
        Assert.Equal(new DateTimeOffset(2026, 10, 23, 17, 25, 47, TimeSpan.Zero).AddTicks(3100347), result.Events[0].OccurredAt);
        Assert.Equal(TimeSpan.Zero, result.Events[0].OccurredAt.Offset);
        Assert.Empty(result.Unknown);
    }

    [Fact]
    public void An_unknown_code_is_skipped_and_reported_not_thrown()
    {
        const string json = """
            {"trackingNumber":"FS1","events":[
              {"id":"a","code":"PU","timestamp":"2026-10-23T10:00:00+02:00","location":"Aarhus terminal"},
              {"id":"b","code":"ZZ","timestamp":"2026-10-23T11:00:00+02:00","location":"Somewhere"}]}
            """;

        var result = FastShipAdapter.Parse(json);

        Assert.Single(result.Events);
        Assert.Equal(new[] { "ZZ" }, result.Unknown);
    }
}
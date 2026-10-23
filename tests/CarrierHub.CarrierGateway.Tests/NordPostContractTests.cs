using System.Globalization;
using CarrierHub.CarrierGateway;
using CarrierHub.CarrierGateway.Adapters;

namespace CarrierHub.CarrierGateway.Tests;

public class NordPostContractTests
{
    private static readonly TimeZoneInfo Copenhagen = TimeZoneInfo.FindSystemTimeZoneById("Europe/Copenhagen");

    private static string Xml(string date, string time) => $"""
        <TrackingResponse><Item barcode="NP1"><Event>
          <EventId>NP1:0</EventId><Type>ACCEPTED</Type><Date>{date}</Date><Time>{time}</Time><Place>Aarhus terminal</Place>
        </Event></Item></TrackingResponse>
        """;

    [Theory]
    [InlineData("2026-10-23", "19:25:47", "2026-10-23T17:25:47Z")]   // summer time, +02:00
    [InlineData("2026-10-26", "10:00:00", "2026-10-26T09:00:00Z")]   // winter time, +01:00
    [InlineData("2026-10-25", "02:30:00", "2026-10-25T00:30:00Z")]   // happens twice: we take the first (summer time)
    [InlineData("2027-03-28", "02:30:00", "2027-03-28T01:30:00Z")]   // never happens (02:00 jumps to 03:00): read as 03:30
    public void Local_times_without_an_offset_become_utc(string date, string time, string expectedUtc)
    {
        var e = Assert.Single(NordPostAdapter.Parse(Xml(date, time), Copenhagen).Events);

        Assert.Equal(DateTimeOffset.Parse(expectedUtc, CultureInfo.InvariantCulture), e.OccurredAt);
    }

    [Fact]
    public void Types_become_statuses_and_unknown_types_are_reported()
    {
        const string xml = """
            <TrackingResponse><Item barcode="NP1">
              <Event><EventId>NP1:0</EventId><Type>NOT_DELIVERED</Type><Date>2026-10-23</Date><Time>12:00:00</Time><Place>Customer address</Place></Event>
              <Event><EventId>NP1:1</EventId><Type>CUSTOMS_HOLD</Type><Date>2026-10-23</Date><Time>13:00:00</Time><Place>Hub</Place></Event>
            </Item></TrackingResponse>
            """;

        var result = NordPostAdapter.Parse(xml, Copenhagen);

        Assert.Equal(ShipmentStatus.DeliveryFailed, Assert.Single(result.Events).Status);
        Assert.Equal(new[] { "CUSTOMS_HOLD" }, result.Unknown);
    }
}
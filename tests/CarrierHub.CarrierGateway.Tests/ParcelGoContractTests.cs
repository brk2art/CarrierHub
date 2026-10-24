using CarrierHub.CarrierGateway.Webhooks;

namespace CarrierHub.CarrierGateway.Tests;

public class ParcelGoContractTests
{
    private const string Secret = "test-secret";
    private const long Timestamp = 1792800000;   // 24 October 2026, 00:00:00 UTC
    private const string Body = """{"event_id":"pg_PG1_0","tracking_number":"PG1","status":"picked_up","occurred_at":1792800000,"location":"Aarhus terminal"}""";

    // Computed outside .NET (Python's hmac module), so the test doesn't just check our code against itself.
    private const string KnownSignature = "56b11e3ef883fff65b19a2b2718dd91eb63c41e364dddf7740c9e40c830eddcd";

    private static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(Timestamp).AddSeconds(30);

    private static string Header(string signature, long timestamp = Timestamp) => $"t={timestamp},v1={signature}";

    [Fact]
    public void Our_signature_matches_an_independently_computed_one() =>
        Assert.Equal(KnownSignature, ParcelGoSignature.Compute(Secret, Timestamp, Body));

    [Fact]
    public void A_correctly_signed_webhook_is_accepted() =>
        Assert.True(ParcelGoSignature.IsValid(Header(KnownSignature), Body, Secret, Now, Tolerance));

    [Fact]
    public void A_changed_body_is_rejected() =>
        Assert.False(ParcelGoSignature.IsValid(Header(KnownSignature), Body.Replace("picked_up", "delivered"), Secret, Now, Tolerance));

    [Fact]
    public void The_wrong_secret_is_rejected() =>
        Assert.False(ParcelGoSignature.IsValid(Header(ParcelGoSignature.Compute("guessed", Timestamp, Body)), Body, Secret, Now, Tolerance));

    [Fact]
    public void A_genuine_webhook_replayed_later_is_rejected() =>
        Assert.False(ParcelGoSignature.IsValid(Header(KnownSignature), Body, Secret, Now.AddMinutes(10), Tolerance));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v1=56b11e3e")]
    [InlineData("t=notanumber,v1=56b11e3e")]
    public void A_missing_or_broken_header_is_rejected(string? header) =>
        Assert.False(ParcelGoSignature.IsValid(header, Body, Secret, Now, Tolerance));

    [Fact]
    public void Statuses_and_unix_times_are_translated()
    {
        var (trackingNumber, result) = ParcelGoWebhook.Parse(Body);

        Assert.Equal("PG1", trackingNumber);
        var e = Assert.Single(result.Events);
        Assert.Equal("pg_PG1_0", e.CarrierEventId);
        Assert.Equal(ShipmentStatus.PickedUp, e.Status);
        Assert.Equal(new DateTimeOffset(2026, 10, 24, 0, 0, 0, TimeSpan.Zero), e.OccurredAt);
    }

    [Fact]
    public void An_unknown_status_is_reported_not_thrown()
    {
        var (_, result) = ParcelGoWebhook.Parse(Body.Replace("picked_up", "held_at_customs"));

        Assert.Empty(result.Events);
        Assert.Equal(new[] { "held_at_customs" }, result.Unknown);
    }
}
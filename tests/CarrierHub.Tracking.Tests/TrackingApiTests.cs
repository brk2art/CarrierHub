using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CarrierHub.Tracking.Api;
using CarrierHub.Tracking.Domain;
using Xunit;

namespace CarrierHub.Tracking.Tests;

// The real endpoints against a real PostgreSQL. Every test uses its own merchant and tracking
// numbers, so the tests never see each other's data in the shared database.
public class TrackingApiTests(TrackingApiFixture fixture) : IClassFixture<TrackingApiFixture>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private HttpClient Client => fixture.Client;

    private static string NewId(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    [Fact]
    public async Task A_created_shipment_can_be_read_back()
    {
        var merchant = NewId("m");
        var created = await CreateShipmentAsync(merchant, NewId("order"), NewId("t"), TimeSpan.FromDays(2));

        var read = await GetShipmentAsync(merchant, created.Id);

        Assert.Equal(created.Id, read.Id);
        Assert.Equal(ShipmentStatus.Created, read.Status);
        Assert.Empty(read.Timeline);
    }

    [Fact]
    public async Task Out_of_order_and_duplicate_events_give_the_right_timeline()
    {
        var merchant = NewId("m");
        var tracking = NewId("t");
        var created = await CreateShipmentAsync(merchant, NewId("order"), tracking, TimeSpan.FromDays(2));
        var t0 = fixture.Clock.GetUtcNow();

        var delivered = await SendEventAsync(tracking, "e3", ShipmentStatus.Delivered, t0.AddHours(28));        // arrives first
        var outForDelivery = await SendEventAsync(tracking, "e2", ShipmentStatus.OutForDelivery, t0.AddHours(24));
        var again = await SendEventAsync(tracking, "e2", ShipmentStatus.OutForDelivery, t0.AddHours(24));

        Assert.True(delivered.StatusChanged);
        Assert.False(outForDelivery.StatusChanged);
        Assert.Equal(EventResult.Duplicate, again.Result);

        var shipment = await GetShipmentAsync(merchant, created.Id);
        Assert.Equal(ShipmentStatus.Delivered, shipment.Status);
        Assert.Equal(new[] { ShipmentStatus.OutForDelivery, ShipmentStatus.Delivered }, shipment.Timeline.Select(e => e.Status));
    }

    [Fact]
    public async Task Another_merchant_cannot_see_the_shipment()
    {
        var reference = NewId("order");
        var created = await CreateShipmentAsync(NewId("m"), reference, NewId("t"), TimeSpan.FromDays(2));
        var otherMerchant = NewId("m");

        var byId = await Client.GetAsync($"/merchants/{otherMerchant}/shipments/{created.Id}");
        var search = await Client.GetFromJsonAsync<List<ShipmentResponse>>(
            $"/merchants/{otherMerchant}/shipments?reference={reference}", Json);

        Assert.Equal(HttpStatusCode.NotFound, byId.StatusCode);   // not 403: it doesn't even confirm the shipment exists
        Assert.Empty(search!);
    }

    [Fact]
    public async Task Retrying_a_create_returns_the_same_shipment()
    {
        var merchant = NewId("m");
        var reference = NewId("order");
        var tracking = NewId("t");
        var promisedBy = fixture.Clock.GetUtcNow().AddDays(2);

        var first = await Client.PostAsJsonAsync($"/merchants/{merchant}/shipments",
            new CreateShipmentRequest(reference, "FastShip", tracking, promisedBy), Json);
        var retry = await Client.PostAsJsonAsync($"/merchants/{merchant}/shipments",
            new CreateShipmentRequest(reference, "FastShip", tracking, promisedBy), Json);
        var clash = await Client.PostAsJsonAsync($"/merchants/{merchant}/shipments",
            new CreateShipmentRequest(reference, "FastShip", NewId("t"), promisedBy), Json);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal((await Read<ShipmentResponse>(first)).Id, (await Read<ShipmentResponse>(retry)).Id);
        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);   // same order number, different parcel
    }

    [Fact]
    public async Task The_same_event_sent_five_times_at_once_is_stored_once()
    {
        var merchant = NewId("m");
        var tracking = NewId("t");
        var created = await CreateShipmentAsync(merchant, NewId("order"), tracking, TimeSpan.FromDays(2));
        var at = fixture.Clock.GetUtcNow().AddHours(1);

        var results = await Task.WhenAll(Enumerable.Range(0, 5)
            .Select(_ => SendEventAsync(tracking, "e1", ShipmentStatus.PickedUp, at)));

        Assert.Equal(1, results.Count(r => r.Result == EventResult.Added));
        Assert.Equal(4, results.Count(r => r.Result == EventResult.Duplicate));
        Assert.Single((await GetShipmentAsync(merchant, created.Id)).Timeline);
    }

    [Fact]
    public async Task Five_different_events_at_once_all_count_and_give_the_right_status()
    {
        var merchant = NewId("m");
        var tracking = NewId("t");
        var created = await CreateShipmentAsync(merchant, NewId("order"), tracking, TimeSpan.FromDays(2));
        var at = fixture.Clock.GetUtcNow();
        ShipmentStatus[] statuses =
        [
            ShipmentStatus.PickedUp, ShipmentStatus.InTransit, ShipmentStatus.OutForDelivery,
            ShipmentStatus.DeliveryFailed, ShipmentStatus.OutForDelivery,
        ];

        var results = await Task.WhenAll(statuses.Select((status, i) =>
            SendEventAsync(tracking, $"e{i + 1}", status, at.AddMinutes(i + 1))));

        Assert.All(results, r => Assert.Equal(EventResult.Added, r.Result));
        var shipment = await GetShipmentAsync(merchant, created.Id);
        Assert.Equal(5, shipment.Timeline.Count);
        // Without the concurrency check, the last request to save could write a status
        // calculated from only some of the events. The latest event is "out for delivery" (e5).
        Assert.Equal(ShipmentStatus.OutForDelivery, shipment.Status);
    }

    [Fact]
    public async Task Late_search_finds_shipments_past_their_promise()
    {
        var merchant = NewId("m");
        var dueTomorrow = await CreateShipmentAsync(merchant, NewId("order"), NewId("t"), TimeSpan.FromDays(1));
        await CreateShipmentAsync(merchant, NewId("order"), NewId("t"), TimeSpan.FromDays(5));

        fixture.Clock.Advance(TimeSpan.FromDays(2));   // two days pass, instantly

        var late = await Client.GetFromJsonAsync<List<ShipmentResponse>>($"/merchants/{merchant}/shipments?late=true", Json);

        Assert.NotNull(late);
        Assert.Equal(new[] { dueTomorrow.Id }, late!.Select(s => s.Id));
        Assert.True(late[0].IsLate);
    }

    [Fact]
    public async Task Active_shipments_leave_out_delivered_ones()
    {
        var merchant = NewId("m");
        var activeTracking = NewId("t");
        var deliveredTracking = NewId("t");
        await CreateShipmentAsync(merchant, NewId("order"), activeTracking, TimeSpan.FromDays(2));
        await CreateShipmentAsync(merchant, NewId("order"), deliveredTracking, TimeSpan.FromDays(2));
        await SendEventAsync(deliveredTracking, "e1", ShipmentStatus.Delivered, fixture.Clock.GetUtcNow().AddHours(1));

        // Lower-case key, while the shipments were created as "FastShip".
        var active = await Client.GetFromJsonAsync<List<ActiveShipmentResponse>>("/carriers/fastship/active-shipments", Json);

        Assert.NotNull(active);
        Assert.Contains(active, s => s.TrackingNumber == activeTracking && s.Carrier == "FastShip");
        Assert.DoesNotContain(active, s => s.TrackingNumber == deliveredTracking);
    }

    private async Task<ShipmentResponse> CreateShipmentAsync(string merchant, string reference, string tracking, TimeSpan promisedIn)
    {
        var response = await Client.PostAsJsonAsync($"/merchants/{merchant}/shipments",
            new CreateShipmentRequest(reference, "FastShip", tracking, fixture.Clock.GetUtcNow() + promisedIn), Json);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await Read<ShipmentResponse>(response);
    }

    private async Task<ShipmentResponse> GetShipmentAsync(string merchant, Guid id) =>
        (await Client.GetFromJsonAsync<ShipmentResponse>($"/merchants/{merchant}/shipments/{id}", Json))!;

    private async Task<CarrierEventResponse> SendEventAsync(string tracking, string eventId, ShipmentStatus status, DateTimeOffset occurredAt)
    {
        var response = await Client.PostAsJsonAsync("/carrier-events",
            new CarrierEventRequest("FastShip", tracking, eventId, status, occurredAt, null, null), Json);
        response.EnsureSuccessStatusCode();
        return await Read<CarrierEventResponse>(response);
    }

    private static async Task<T> Read<T>(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>(Json))!;
}
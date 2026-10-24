using System.Net;
using CarrierHub.Notification.Data;
using CarrierHub.Notification.Webhooks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CarrierHub.Notification.Tests;

public class WebhookDelivererTests
{
    private const string Payload = """{"merchantId":"merchant-1","status":"PickedUp","shipmentId":"s-1"}""";

    // A merchant's server: records what it receives and answers with a fixed status.
    private sealed class FakeMerchant(HttpStatusCode status) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Received { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Received.Add((request, await request.Content!.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(status);
        }
    }

    // In memory: these tests are about the delivery rules, not about PostgreSQL.
    private static NotificationDbContext NewDb() =>
        new(new DbContextOptionsBuilder<NotificationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<NotificationDbContext> DbWithWebhookAsync(string secret = "merchant-secret")
    {
        var db = NewDb();
        db.Webhooks.Add(new WebhookSubscription
        {
            MerchantId = "merchant-1",
            Url = "https://merchant.example/hooks",
            Secret = secret,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return db;
    }

    private static WebhookDeliverer Deliverer(NotificationDbContext db, FakeMerchant merchant) =>
        new(db, new HttpClient(merchant), TimeProvider.System, NullLogger<WebhookDeliverer>.Instance);

    private static Task<DeliveryResult> Deliver(WebhookDeliverer deliverer, string messageId = "msg-1") =>
        deliverer.DeliverAsync(messageId, "ShipmentStatusChanged", "merchant-1", Payload, CancellationToken.None);

    [Fact]
    public async Task Without_a_webhook_nothing_is_sent()
    {
        var merchant = new FakeMerchant(HttpStatusCode.OK);

        var result = await Deliver(Deliverer(NewDb(), merchant));

        Assert.Equal(DeliveryResult.NoWebhook, result);
        Assert.Empty(merchant.Received);
    }

    [Fact]
    public async Task A_delivery_is_signed_so_the_merchant_can_check_it()
    {
        var merchant = new FakeMerchant(HttpStatusCode.OK);

        var result = await Deliver(Deliverer(await DbWithWebhookAsync("merchant-secret"), merchant));

        Assert.Equal(DeliveryResult.Delivered, result);
        var (request, body) = Assert.Single(merchant.Received);
        Assert.Equal("msg-1", request.Headers.GetValues("CarrierHub-Event-Id").Single());

        // Check it the way a merchant would: recompute the HMAC from the header's timestamp and the raw body.
        var parts = request.Headers.GetValues(WebhookSignature.Header).Single()
            .Split(',')
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0], p => p[1]);
        Assert.Equal(WebhookSignature.Compute("merchant-secret", long.Parse(parts["t"]), body), parts["v1"]);
        Assert.Contains("\"merchantId\":\"merchant-1\"", body);
    }

    [Fact]
    public async Task A_message_already_delivered_is_not_sent_again()
    {
        var merchant = new FakeMerchant(HttpStatusCode.OK);
        var deliverer = Deliverer(await DbWithWebhookAsync(), merchant);

        await Deliver(deliverer);
        var second = await Deliver(deliverer);   // the outbox delivers at least once: the same message can come back

        Assert.Equal(DeliveryResult.AlreadyDelivered, second);
        Assert.Single(merchant.Received);
    }

    [Fact]
    public async Task A_failure_is_recorded_and_reported()
    {
        var db = await DbWithWebhookAsync();

        var result = await Deliver(Deliverer(db, new FakeMerchant(HttpStatusCode.InternalServerError)));

        Assert.Equal(DeliveryResult.Failed, result);
        var delivery = Assert.Single(db.Deliveries);
        Assert.Equal(500, delivery.StatusCode);
        Assert.False(delivery.Succeeded);
    }

    [Fact]
    public void The_signature_matches_an_independently_computed_one() =>
        // The same scheme and test vector as ParcelGo's (computed with Python's hmac module): CarrierHub signs
        // its webhooks exactly the way it expects carriers to sign theirs.
        Assert.Equal("56b11e3ef883fff65b19a2b2718dd91eb63c41e364dddf7740c9e40c830eddcd",
            WebhookSignature.Compute("test-secret", 1792800000,
                """{"event_id":"pg_PG1_0","tracking_number":"PG1","status":"picked_up","occurred_at":1792800000,"location":"Aarhus terminal"}"""));
}
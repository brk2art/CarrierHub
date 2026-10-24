using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CarrierHub.PublicApi.V1;

namespace CarrierHub.PublicApi.Tests;

public class PublicApiTests : IClassFixture<PublicApiFactory>
{
    private const string ShipmentId = "0199f0a0-0000-7000-8000-000000000001";

    // What Tracking returns, including receivedAt, which merchants must never see.
    private const string TrackingShipment = """
        {"id":"0199f0a0-0000-7000-8000-000000000001","merchantReference":"ORDER-1","carrier":"FastShip",
         "trackingNumber":"FS1","status":"Delivered","promisedBy":"2026-10-25T16:00:00+00:00",
         "deliveredAt":"2026-10-24T12:10:00+00:00","isLate":false,
         "timeline":[{"status":"Delivered","occurredAt":"2026-10-24T12:10:00+00:00",
                      "receivedAt":"2026-10-24T12:15:00+00:00","location":"Copenhagen","description":null}]}
        """;

    private readonly PublicApiFactory _api;
    private readonly HttpClient _client;

    public PublicApiTests(PublicApiFactory api)
    {
        _api = api;
        _client = api.CreateClient();
        _api.Tracking.Reset();
        _api.Notification.Reset();
    }

    // Every test uses its own merchants, so they never share a rate-limit bucket.
    private static string NewMerchant() => $"m-{Guid.NewGuid():N}";

    private static CreateShipmentRequest NewShipment() =>
        new("ORDER-1", "FastShip", "FS1", DateTimeOffset.UtcNow.AddDays(2));

    [Fact]
    public async Task Without_a_token_the_answer_is_401_and_tracking_is_not_called()
    {
        var response = await _client.GetAsync("/v1/shipments");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_api.Tracking.Paths);
    }

    [Fact]
    public async Task The_merchant_comes_from_the_token_not_from_the_request()
    {
        var merchant = NewMerchant();
        var token = TestTokens.Create(merchant, Scopes.Read);

        // An attempt to ask for someone else's shipments through the query string.
        await SendAsync(HttpMethod.Get, "/v1/shipments?reference=ORDER-1&merchantId=someone-else", token);

        Assert.Equal($"/merchants/{merchant}/shipments?reference=ORDER-1", Assert.Single(_api.Tracking.Paths));
    }

    [Fact]
    public async Task A_read_only_token_cannot_create()
    {
        var token = TestTokens.Create(NewMerchant(), Scopes.Read);

        var response = await SendAsync(HttpMethod.Post, "/v1/shipments", token, NewShipment());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_api.Tracking.Paths);
    }

    [Fact]
    public async Task A_token_without_a_merchant_is_403()
    {
        var token = TestTokens.Create(merchantId: null, Scopes.Read);

        var response = await SendAsync(HttpMethod.Get, "/v1/shipments", token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_api.Tracking.Paths);
    }

    [Fact]
    public async Task Internal_fields_from_tracking_do_not_reach_the_merchant()
    {
        _api.Tracking.Respond = _ => FakeTracking.Json(HttpStatusCode.OK, TrackingShipment);
        var token = TestTokens.Create(NewMerchant(), Scopes.Read);

        var response = await SendAsync(HttpMethod.Get, $"/v1/shipments/{ShipmentId}", token);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"location\":\"Copenhagen\"", body);
        Assert.DoesNotContain("receivedAt", body);
    }

    [Fact]
    public async Task A_created_shipment_gets_a_public_location()
    {
        _api.Tracking.Respond = _ => FakeTracking.Json(HttpStatusCode.Created, TrackingShipment);
        var token = TestTokens.Create(NewMerchant(), Scopes.Write);

        var response = await SendAsync(HttpMethod.Post, "/v1/shipments", token, NewShipment());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal($"/v1/shipments/{ShipmentId}", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task When_tracking_is_down_the_answer_is_502_without_internal_details()
    {
        _api.Tracking.Respond = _ => throw new HttpRequestException("No such host is known. (tracking:443)");
        var token = TestTokens.Create(NewMerchant(), Scopes.Read);

        var response = await SendAsync(HttpMethod.Get, "/v1/shipments", token);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.DoesNotContain("tracking:443", body);
    }

    [Fact]
    public async Task The_rate_limit_is_per_merchant()
    {
        var busy = TestTokens.Create(NewMerchant(), Scopes.Read);
        var quiet = TestTokens.Create(NewMerchant(), Scopes.Read);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
            statuses.Add((await SendAsync(HttpMethod.Get, "/v1/shipments", busy)).StatusCode);
        var other = await SendAsync(HttpMethod.Get, "/v1/shipments", quiet);

        Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests }, statuses);
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);   // the busy merchant didn't slow this one down
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string? token, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return _client.SendAsync(request);
    }

    [Fact]
    public async Task Registering_a_webhook_uses_the_merchant_from_the_token_and_passes_the_secret_on()
    {
        _api.Notification.Respond = _ => FakeTracking.Json(HttpStatusCode.Created,
            """{"merchantId":"internal","url":"https://merchant.example/hooks","secret":"s3cret"}""");
        var merchant = NewMerchant();

        var response = await SendAsync(HttpMethod.Put, "/v1/webhook", TestTokens.Create(merchant, Scopes.Write),
            new RegisterWebhookRequest("https://merchant.example/hooks"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal($"/merchants/{merchant}/webhook", Assert.Single(_api.Notification.Paths));
        Assert.Contains("\"secret\":\"s3cret\"", body);
        Assert.DoesNotContain("merchantId", body);   // internal field, not part of v1
    }

    [Fact]
    public async Task A_read_only_token_cannot_change_the_webhook()
    {
        var response = await SendAsync(HttpMethod.Put, "/v1/webhook", TestTokens.Create(NewMerchant(), Scopes.Read),
            new RegisterWebhookRequest("https://merchant.example/hooks"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_api.Notification.Paths);
    }
}
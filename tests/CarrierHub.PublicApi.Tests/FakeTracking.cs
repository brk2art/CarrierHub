using System.Net;
using System.Text;

namespace CarrierHub.PublicApi.Tests;

// Stands in for the Tracking service: records what the Public API asked for, and answers as told.
public sealed class FakeTracking : HttpMessageHandler
{
    private readonly List<string> _paths = [];

    public IReadOnlyList<string> Paths { get { lock (_paths) return _paths.ToList(); } }

    public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => Json(HttpStatusCode.OK, "[]");

    public void Reset()
    {
        lock (_paths) _paths.Clear();
        Respond = _ => Json(HttpStatusCode.OK, "[]");
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (_paths) _paths.Add(request.RequestUri!.PathAndQuery);
        return Task.FromResult(Respond(request));
    }
}
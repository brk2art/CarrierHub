using System.Net.Http.Headers;
using Azure.Core;

namespace CarrierHub.OpsAssistant;

// Adds an Entra token to every request, and fetches a new one shortly before it expires.
public sealed class BearerTokenHandler(TokenCredential credential, string scope) : DelegatingHandler
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AccessToken _token;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if(_token.ExpiresOn < DateTimeOffset.UtcNow.AddMinutes(5))
        {
            await _gate.WaitAsync(ct);
            try
            {
                if (_token.ExpiresOn < DateTimeOffset.UtcNow.AddMinutes(5))
                    _token = await credential.GetTokenAsync(new TokenRequestContext([scope]), ct);
            }
            finally { _gate.Release(); }
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token.Token);
        return await base.SendAsync(request, ct);
    }
}

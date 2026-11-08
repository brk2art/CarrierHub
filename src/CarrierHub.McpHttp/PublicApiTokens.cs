using Microsoft.Identity.Client;

namespace CarrierHub.McpHttp;

// On-behalf-of: the user's token FOR THIS SERVER goes in, a token FOR THE PUBLIC API, for the same user, comes out.
// This server proves who it is with its own client secret (in Azure: a managed identity instead). MSAL keeps the
// new token in memory, so later calls with the same user token are answered without asking Entra again.
public sealed class PublicApiTokens(IConfidentialClientApplication entra, string publicApiScope)
{
    public async Task<string> GetAsync(string userToken, CancellationToken ct)
    {
        var result = await entra
            .AcquireTokenOnBehalfOf([publicApiScope], new UserAssertion(userToken))
            .ExecuteAsync(ct);
        return result.AccessToken;
    }
}

using System.ComponentModel;
using System.Text.Json.Serialization;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.AI;

namespace CarrierHub.AssistantTools;

// One section of a carrier's service terms, as stored in the "carrier-policies" index.
public sealed record PolicySection(
    [property: JsonPropertyName("carrier")] string Carrier,
    [property: JsonPropertyName("section")] string Section,
    [property: JsonPropertyName("content")] string Content);

// Searches the carriers' service terms. No token: the terms are the same for every merchant, so one instance can
// serve every question. The search key can only search; the index is built by CarrierHub.PolicyIndexer.
public sealed class PolicyTools(SearchClient search, IEmbeddingGenerator<string, Embedding<float>> embedder)
{
    public const string IndexName = "carrier-policies";

    // The carriers CarrierHub integrates with. A carrier the model names must be one of these: it never reaches
    // the search filter otherwise.
    public static readonly string[] Carriers = ["FastShip", "NordPost", "ParcelGo"];

    // Three, not one: when keyword and vector search disagree (a tie), the right section is still among them.
    private const int Top = 3;

    [Description("Searches the carriers' service terms: late delivery compensation, failed delivery attempts, lost " +
                 "and damaged parcels, claim deadlines and what is not compensated. Use it for any question about " +
                 "what a carrier pays, from when, by when, or what a claim needs. Give the carrier when the question " +
                 "is about one carrier or one shipment.")]
    public async Task<object> SearchCarrierPolicies(
        [Description("What to look up, in plain words, for example \"compensation for a parcel delivered 3 hours late\".")]
        string question,
        [Description("FastShip, NordPost or ParcelGo. When the question is about a shipment, always give that " +
                     "shipment's carrier, taken from its data. Leave it out only to compare all three carriers.")]
        string? carrier = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(question))
            return new { error = "A question is required." };

        string? known = null;
        if (!string.IsNullOrWhiteSpace(carrier))
        {
            known = Carriers.FirstOrDefault(c => c.Equals(carrier.Trim(), StringComparison.OrdinalIgnoreCase));
            if (known is null)
                return new { error = $"Unknown carrier '{carrier}'. Known carriers: {string.Join(", ", Carriers)}." };
        }

        // Hybrid search, as in DocChat: the question's words for keyword search, its embedding for vector search.
        var vector = (await embedder.GenerateAsync([question], cancellationToken: ct))[0].Vector;
        var options = new SearchOptions
        {
            Size = Top,
            VectorSearch = new VectorSearchOptions
            {
                Queries = { new VectorizedQuery(vector) { KNearestNeighborsCount = Top, Fields = { "contentVector" } } },
            },
        };
        if (known is not null)
            options.Filter = SearchFilter.Create($"carrier eq {known}");   // quoted and escaped, like a SQL parameter

        foreach (var field in new[] { "carrier", "section", "content" })
            options.Select.Add(field);   // never the vector: nobody reads 1,536 numbers

        var response = await search.SearchAsync<PolicySection>(question, options, ct);

        var sections = new List<object>();
        await foreach (var result in response.Value.GetResultsAsync())
            sections.Add(new { source = $"{result.Document.Carrier}: {result.Document.Section}", text = result.Document.Content });

        return sections.Count == 0
            ? new { error = "No matching terms were found." }
            : new { sections };
    }
}
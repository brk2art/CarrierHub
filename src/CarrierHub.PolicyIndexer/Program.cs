using System.Text;
using System.Text.Json.Serialization;
using Azure;
using Azure.AI.OpenAI;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

// Builds the "carrier-policies" index from docs/carriers/*.md: DocChat's chunking and hybrid index, plus a carrier
// field so a search can be limited to one carrier.
//   dotnet run --project src/CarrierHub.PolicyIndexer [-- docs/carriers]
// Settings come from the Ops Assistant's user secrets (Assistant:AzureOpenAI:*, Assistant:Search:*).

const string IndexName = "carrier-policies";
const int Dimensions = 1536; // what text-embedding-3-small produces

var config = new ConfigurationBuilder().AddUserSecrets<Program>().Build();
string Setting(string key) => config[key] ?? throw new InvalidOperationException($"Set {key} in user secrets.");

// 1. Read the documents and cut them into chunks, one per "## " section.
var folder = args.Length > 0 ? args[0] : Path.Combine("docs", "carriers");
var files = Directory.GetFiles(folder, "*.md").Order().ToList();
var chunks = files.SelectMany(Chunker.Split).ToList();
Console.WriteLine($"{chunks.Count} chunks from {files.Count} documents:");
foreach (var carrier in chunks.GroupBy(c => c.Carrier))
    Console.WriteLine($"  {carrier.Key}: {carrier.Count()} chunks");

// 2. Turn every chunk into a vector, in one call.
var azure = new AzureOpenAIClient(
    new Uri(Setting("Assistant:AzureOpenAI:Endpoint")),
    new AzureKeyCredential(Setting("Assistant:AzureOpenAI:ApiKey")));
var embedder = azure.GetEmbeddingClient(config["Assistant:AzureOpenAI:EmbeddingDeployment"] ?? "embedding")
    .AsIEmbeddingGenerator();

var embeddings = await embedder.GenerateAsync(chunks.Select(c => c.EmbeddingText));
for (var i = 0; i < chunks.Count; i++)
    chunks[i] = chunks[i] with { ContentVector = embeddings[i].Vector.ToArray() };
Console.WriteLine($"Embedded {chunks.Count} chunks ({embeddings.Usage?.InputTokenCount} tokens).");

// 3. Create the index: the same fields as DocChat's, plus "carrier", which a search can filter on.
var indexClient = new SearchIndexClient(
    new Uri(Setting("Assistant:Search:Endpoint")),
    new AzureKeyCredential(Setting("Assistant:Search:AdminKey")));

var index = new SearchIndex(IndexName)
{
    Fields =
    {
        new SimpleField("id", SearchFieldDataType.String) { IsKey = true },
        new SimpleField("carrier", SearchFieldDataType.String) { IsFilterable = true, IsFacetable = true },
        new SimpleField("source", SearchFieldDataType.String) { IsFilterable = true },
        new SearchableField("title"),
        new SearchableField("section"),
        new SearchableField("content"),
        new SearchField("contentVector", SearchFieldDataType.Collection(SearchFieldDataType.Single))
        {
            IsSearchable = true,
            VectorSearchDimensions = Dimensions,
            VectorSearchProfileName = "vector-profile",
        },
    },
    VectorSearch = new VectorSearch
    {
        Algorithms = { new HnswAlgorithmConfiguration("hnsw") },
        Profiles = { new VectorSearchProfile("vector-profile", "hnsw") },
    },
};

// Start from an empty index every run, so chunks of changed or deleted documents don't linger.
try { await indexClient.DeleteIndexAsync(IndexName, CancellationToken.None); }
catch (RequestFailedException e) when (e.Status == 404) { }
await indexClient.CreateIndexAsync(index);

// 4. Upload the chunks.
var searchClient = indexClient.GetSearchClient(IndexName);
var upload = await searchClient.UploadDocumentsAsync(chunks);
Console.WriteLine($"Uploaded {upload.Value.Results.Count(r => r.Succeeded)} of {chunks.Count} chunks to '{IndexName}'.");

// 5. Three checks: hybrid search limited to one carrier, with words that aren't in the documents.
(string Carrier, string Question)[] checks =
[
    ("NordPost", "The parcel is 2 hours late. Do I get money back?"),
    ("FastShip", "Nobody was home when the driver came"),
    ("ParcelGo", "How long do I have to report a broken item?"),
];

foreach (var (carrier, question) in checks)
{
    var vector = (await embedder.GenerateAsync([question]))[0].Vector;
    var options = new SearchOptions
    {
        Size = 2,
        Filter = SearchFilter.Create($"carrier eq {carrier}"),   // quotes and escapes the value safely
        VectorSearch = new VectorSearchOptions
        {
            Queries = { new VectorizedQuery(vector) { KNearestNeighborsCount = 2, Fields = { "contentVector" } } },
        },
    };
    foreach (var field in new[] { "id", "carrier", "source", "title", "section", "content" })
        options.Select.Add(field);

    Console.WriteLine();
    Console.WriteLine($"[{carrier}] {question}");
    var results = await searchClient.SearchAsync<Chunk>(question, options);
    await foreach (var result in results.Value.GetResultsAsync())
        Console.WriteLine($"  {result.Score:0.0000}  {result.Document.Carrier} > {result.Document.Section}");
}

// One chunk = one section of one carrier's terms. The JSON names match the index field names.
public sealed record Chunk(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("carrier")] string Carrier,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("section")] string Section,
    [property: JsonPropertyName("content")] string Content)
{
    [JsonPropertyName("contentVector")]
    public float[]? ContentVector { get; init; }

    // What gets embedded: the chunk plus where it comes from. "Lost parcels" alone could be any carrier's;
    // "NordPost service terms for merchants - Lost parcels" can't.
    [JsonIgnore]
    public string EmbeddingText => $"{Title} - {Section}\n{Content}";
}

public static class Chunker
{
    private const int MaxChars = 1500;

    // Splits a Markdown document at its "## " headings: each section is about one thing, so it makes a good chunk.
    public static List<Chunk> Split(string path)
    {
        var source = Path.GetFileName(path);
        var baseId = Path.GetFileNameWithoutExtension(path);
        var lines = File.ReadAllLines(path);
        var title = lines.FirstOrDefault(l => l.StartsWith("# "))?[2..].Trim() ?? baseId;

        // The carrier is the first word of the title: "NordPost service terms for merchants" → "NordPost".
        var carrier = title.Split(' ', 2)[0];

        var sections = new List<(string Heading, string Text)>();
        var heading = "Introduction";
        var text = new StringBuilder();

        foreach (var line in lines)
        {
            if (line.StartsWith("# "))
                continue;

            if (line.StartsWith("## "))
            {
                AddSection();
                heading = line[3..].Trim();
                continue;
            }

            text.Append(line).Append('\n');
        }
        AddSection();

        var chunks = new List<Chunk>();
        foreach (var (sectionHeading, sectionText) in sections)
            foreach (var piece in Pieces(sectionText))
                chunks.Add(new Chunk($"{baseId}-{chunks.Count + 1}", carrier, source, title, sectionHeading, piece));
        return chunks;

        void AddSection()
        {
            var body = text.ToString().Trim();
            if (body.Length > 0)
                sections.Add((heading, body));
            text.Clear();
        }
    }

    // A section longer than MaxChars is cut between paragraphs, never in the middle of a sentence.
    private static IEnumerable<string> Pieces(string text)
    {
        if (text.Length <= MaxChars)
        {
            yield return text;
            yield break;
        }

        var current = new StringBuilder();
        foreach (var paragraph in text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (current.Length > 0 && current.Length + paragraph.Length > MaxChars)
            {
                yield return current.ToString().Trim();
                current.Clear();
            }
            current.Append(paragraph).Append("\n\n");
        }
        if (current.Length > 0)
            yield return current.ToString().Trim();
    }
}
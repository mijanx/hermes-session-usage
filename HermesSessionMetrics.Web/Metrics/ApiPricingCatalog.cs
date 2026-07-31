using System.Text.Json;

namespace HermesSessionMetrics.Web.Metrics;

public sealed record ApiPriceEntry(
    string Model,
    string Provider,
    decimal InputPerMillion,
    decimal CacheReadPerMillion,
    decimal CacheWritePerMillion,
    decimal OutputPerMillion);

public readonly record struct ApiPriceEstimate(decimal CostUsd, string Provider);

public sealed record ApiPricingSource(
    string Name,
    string Url,
    DateTimeOffset RetrievedAt,
    string? Provider = null,
    string? Basis = null);

public sealed record ApiPricingDocument(
    int SchemaVersion,
    ApiPricingSource Source,
    IReadOnlyList<ApiPriceEntry> Models,
    IReadOnlyList<ApiPricingSource>? Sources = null);

public sealed class ApiPricingCatalog
{
    private readonly IReadOnlyDictionary<string, ApiPriceEntry> _entries;

    private ApiPricingCatalog(
        IReadOnlyDictionary<string, ApiPriceEntry> entries,
        ApiPricingSource? source,
        IReadOnlyList<ApiPricingSource> sources)
    {
        _entries = entries;
        Source = source;
        Sources = sources;
    }

    public static ApiPricingCatalog Empty { get; } = FromEntries([]);

    public ApiPricingSource? Source { get; }

    public IReadOnlyList<ApiPricingSource> Sources { get; }

    public int Count => _entries.Count;

    public static ApiPricingCatalog FromEntries(
        IEnumerable<ApiPriceEntry> entries,
        ApiPricingSource? source = null,
        IReadOnlyList<ApiPricingSource>? sources = null)
    {
        var byModel = entries.ToDictionary(x => x.Model, StringComparer.OrdinalIgnoreCase);
        return new ApiPricingCatalog(byModel, source, sources ?? []);
    }

    public static ApiPricingCatalog Load(string path)
    {
        var json = File.ReadAllText(path);
        var document = JsonSerializer.Deserialize<ApiPricingDocument>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidDataException("The API pricing document is empty.");

        if (document.SchemaVersion != 1)
            throw new InvalidDataException($"Unsupported API pricing schema version {document.SchemaVersion}.");
        if (document.Models.Any(x => string.IsNullOrWhiteSpace(x.Model) ||
                                     x.InputPerMillion < 0 || x.CacheReadPerMillion < 0 ||
                                     x.CacheWritePerMillion < 0 || x.OutputPerMillion < 0))
            throw new InvalidDataException("The API pricing document contains an invalid model entry.");

        return FromEntries(document.Models, document.Source, document.Sources);
    }

    public ApiPriceEstimate? Estimate(
        string model,
        long inputTokens,
        long cacheReadTokens,
        long cacheWriteTokens,
        long outputTokens)
    {
        if (!_entries.TryGetValue(model, out var price)) return null;

        const decimal million = 1_000_000m;
        var cost = (
            inputTokens * price.InputPerMillion +
            cacheReadTokens * price.CacheReadPerMillion +
            cacheWriteTokens * price.CacheWritePerMillion +
            outputTokens * price.OutputPerMillion) / million;
        return new ApiPriceEstimate(cost, price.Provider);
    }
}

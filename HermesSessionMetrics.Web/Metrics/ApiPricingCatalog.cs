using System.Text.Json;

namespace HermesSessionMetrics.Web.Metrics;

public sealed record ApiPriceEntry(
    string Model,
    string Provider,
    decimal InputPerMillion,
    decimal CacheReadPerMillion,
    decimal CacheWritePerMillion,
    decimal OutputPerMillion,
    IReadOnlyList<string>? SourceIds = null,
    string? Basis = null);

public readonly record struct ApiPriceEstimate(decimal CostUsd, string Provider);

public sealed record ApiPricingSource(
    string Name,
    string Url,
    DateTimeOffset RetrievedAt,
    string? Provider = null,
    string? Basis = null,
    string? Id = null);

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

    public IReadOnlyList<ApiPriceEntry> Entries => _entries.Values
        .OrderBy(x => x.Model, StringComparer.OrdinalIgnoreCase)
        .ToArray();

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

        if (document.SchemaVersion is not (1 or 2))
            throw new InvalidDataException($"Unsupported API pricing schema version {document.SchemaVersion}.");
        if (document.Models.Any(x => string.IsNullOrWhiteSpace(x.Model) ||
                                     x.InputPerMillion < 0 || x.CacheReadPerMillion < 0 ||
                                     x.CacheWritePerMillion < 0 || x.OutputPerMillion < 0))
            throw new InvalidDataException("The API pricing document contains an invalid model entry.");

        var sources = document.Sources ?? [];
        var sourceIds = sources
            .Select(x => x.Id)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Cast<string>()
            .ToArray();
        var hasDuplicateSourceIds = sourceIds.Length != sourceIds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var hasUnknownSourceReference = document.Models.Any(x => x.SourceIds is { Count: > 0 } &&
            x.SourceIds.Any(id => !sourceIds.Contains(id, StringComparer.OrdinalIgnoreCase)));
        var lacksRequiredProvenance = document.SchemaVersion == 2 &&
            (sourceIds.Length != sources.Count || document.Models.Any(x =>
                x.SourceIds is not { Count: > 0 } || string.IsNullOrWhiteSpace(x.Basis)));
        if (hasDuplicateSourceIds || hasUnknownSourceReference || lacksRequiredProvenance)
            throw new InvalidDataException("The API pricing document contains invalid provenance references.");

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

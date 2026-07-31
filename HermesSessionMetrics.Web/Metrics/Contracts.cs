namespace HermesSessionMetrics.Web.Metrics;

public sealed record ProfileDatabase(string Name, string DatabasePath);

public sealed record MetricsQuery(
    int? Hours,
    string? Search,
    int Limit,
    int Offset,
    string Sort = "tokens",
    bool Descending = true,
    string CostBasis = "api-equivalent");

public sealed record UsageLine(
    string Model,
    string Provider,
    string BillingMode,
    string Task,
    long ApiCalls,
    long InputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    long OutputTokens,
    long ReasoningTokens,
    long AccountedTokens,
    double EstimatedCostUsd,
    double ActualCostUsd,
    double? ApiEquivalentCostUsd,
    string? ApiEquivalentPricingProvider,
    string? CostStatus,
    string? CostSource);

public sealed record SessionMetrics(
    string Profile,
    string Id,
    string Source,
    string Title,
    string PrimaryModel,
    string? ParentSessionId,
    bool IsChild,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string Status,
    long MessageCount,
    long ToolCallCount,
    long AccountedTokens,
    long ReasoningTokens,
    long ApiCalls,
    double EstimatedCostUsd,
    double ActualCostUsd,
    double ApiEquivalentCostUsd,
    long ApiEquivalentPricedTokens,
    long ApiEquivalentUnpricedTokens,
    IReadOnlyList<UsageLine> UsageLines);

public sealed record SessionFamilyMetrics(
    string Profile,
    string RootSessionId,
    bool RootIncluded,
    DateTimeOffset StartedAt,
    long AccountedTokens,
    long ReasoningTokens,
    long ApiCalls,
    double EstimatedCostUsd,
    double ActualCostUsd,
    double ApiEquivalentCostUsd,
    long ApiEquivalentPricedTokens,
    long ApiEquivalentUnpricedTokens,
    IReadOnlyList<SessionMetrics> Sessions,
    IReadOnlyList<UsageLine> UsageLines);

public sealed record MetricsResult(
    int SchemaVersion,
    DateTimeOffset GeneratedAt,
    DateTimeOffset? Cutoff,
    int? Hours,
    IReadOnlyList<string> Profiles,
    int TotalSessions,
    int FilteredSessions,
    int FilteredFamilies,
    long AccountedTokens,
    long ReasoningTokens,
    long ApiCalls,
    double EstimatedCostUsd,
    double ActualCostUsd,
    double ApiEquivalentCostUsd,
    long ApiEquivalentPricedTokens,
    long ApiEquivalentUnpricedTokens,
    ApiPricingSource? ApiPricingSource,
    IReadOnlyList<SessionFamilyMetrics> Families,
    long QueryElapsedMilliseconds);

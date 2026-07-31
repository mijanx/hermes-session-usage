using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace HermesSessionMetrics.Web.Metrics;

public sealed class MetricsQueryService(ApiPricingCatalog pricing, Func<DateTimeOffset>? clock = null)
{
    private readonly ApiPricingCatalog _pricing = pricing;
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);

    public async Task<MetricsResult> QueryAsync(
        IReadOnlyList<ProfileDatabase> profiles,
        MetricsQuery query,
        CancellationToken cancellationToken)
    {
        if (query.Hours is not (24 or 168 or 720))
            throw new ArgumentOutOfRangeException(nameof(query), "Window must be 24, 168, or 720 hours.");

        var watch = Stopwatch.StartNew();
        var now = _clock();
        var cutoff = now.AddHours(-query.Hours);
        var totalSessions = 0;
        var sessions = new List<SessionMetrics>();

        foreach (var profile in profiles)
        {
            var result = await QueryProfileAsync(profile, cutoff, query.Search, cancellationToken);
            totalSessions += result.TotalSessions;
            sessions.AddRange(result.Sessions);
        }

        var ordered = Order(sessions, query.Sort, query.Descending, query.CostBasis).ToArray();
        var filteredSessions = ordered.Length;
        var limit = Math.Clamp(query.Limit, 1, 1_000);
        var offset = Math.Max(query.Offset, 0);
        var page = ordered.Skip(offset).Take(limit).ToArray();

        watch.Stop();
        return new MetricsResult(
            now,
            cutoff,
            query.Hours,
            profiles.Select(x => x.Name).ToArray(),
            totalSessions,
            filteredSessions,
            sessions.Sum(x => x.AccountedTokens),
            sessions.Sum(x => x.ReasoningTokens),
            sessions.Sum(x => x.ApiCalls),
            sessions.Sum(x => x.EstimatedCostUsd),
            sessions.Sum(x => x.ActualCostUsd),
            sessions.Sum(x => x.ApiEquivalentCostUsd),
            sessions.Sum(x => x.ApiEquivalentPricedTokens),
            sessions.Sum(x => x.ApiEquivalentUnpricedTokens),
            _pricing.Source,
            page,
            watch.ElapsedMilliseconds);
    }

    private static IOrderedEnumerable<SessionMetrics> Order(
        IEnumerable<SessionMetrics> sessions,
        string sort,
        bool descending,
        string costBasis)
    {
        Func<SessionMetrics, IComparable> key = sort.ToLowerInvariant() switch
        {
            "started" => x => x.StartedAt,
            "cost" when costBasis.Equals("api-equivalent", StringComparison.OrdinalIgnoreCase) => x => x.ApiEquivalentCostUsd,
            "cost" => x => x.EstimatedCostUsd,
            "calls" => x => x.ApiCalls,
            _ => x => x.AccountedTokens
        };

        return descending
            ? sessions.OrderByDescending(key).ThenByDescending(x => x.StartedAt)
            : sessions.OrderBy(key).ThenBy(x => x.StartedAt);
    }

    private async Task<ProfileResult> QueryProfileAsync(
        ProfileDatabase profile,
        DateTimeOffset cutoff,
        string? search,
        CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = profile.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            DefaultTimeout = 5
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA query_only=ON; PRAGMA busy_timeout=5000;";
            await pragma.ExecuteNonQueryAsync(cancellationToken);
        }

        if (!await HasTableAsync(connection, "sessions", cancellationToken) ||
            !await HasTableAsync(connection, "session_model_usage", cancellationToken))
            return new ProfileResult(0, []);

        var totalCommand = connection.CreateCommand();
        totalCommand.CommandText = "SELECT COUNT(*) FROM sessions";
        var totalSessions = Convert.ToInt32(await totalCommand.ExecuteScalarAsync(cancellationToken));

        var command = connection.CreateCommand();
        command.CommandText = """
            WITH matching_sessions AS (
                SELECT s.id, s.source, s.model, s.parent_session_id, s.started_at, s.ended_at,
                       s.end_reason, s.message_count, s.tool_call_count, s.api_call_count,
                       COALESCE(NULLIF(s.title, ''), NULLIF(s.display_name, ''), s.id) AS title
                FROM sessions s
                WHERE (
                    s.started_at >= $cutoff OR EXISTS (
                        SELECT 1 FROM session_model_usage recent
                        WHERE recent.session_id = s.id
                          AND COALESCE(recent.last_seen, recent.first_seen, s.started_at) >= $cutoff
                    )
                )
                AND (
                    $search = '' OR s.id LIKE $pattern OR COALESCE(s.title, '') LIKE $pattern
                    OR COALESCE(s.display_name, '') LIKE $pattern OR COALESCE(s.source, '') LIKE $pattern
                    OR COALESCE(s.model, '') LIKE $pattern OR EXISTS (
                        SELECT 1 FROM session_model_usage searched
                        WHERE searched.session_id = s.id
                          AND (searched.model LIKE $pattern OR searched.task LIKE $pattern OR searched.billing_provider LIKE $pattern)
                    )
                )
            )
            SELECT s.id, s.source, s.model, s.parent_session_id, s.started_at, s.ended_at,
                   s.end_reason, s.message_count, s.tool_call_count, s.api_call_count, s.title,
                   u.model, u.billing_provider, u.billing_mode, u.task, u.api_call_count,
                   u.input_tokens, u.cache_read_tokens, u.cache_write_tokens, u.output_tokens,
                   u.reasoning_tokens, u.estimated_cost_usd, u.actual_cost_usd,
                   u.cost_status, u.cost_source
            FROM matching_sessions s
            LEFT JOIN session_model_usage u ON u.session_id = s.id
            ORDER BY s.started_at DESC
            """;
        command.Parameters.AddWithValue("$cutoff", cutoff.ToUnixTimeMilliseconds() / 1000.0);
        var normalizedSearch = (search ?? string.Empty).Trim();
        command.Parameters.AddWithValue("$search", normalizedSearch);
        command.Parameters.AddWithValue("$pattern", $"%{normalizedSearch}%");

        var builders = new Dictionary<string, SessionBuilder>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetString(0);
            if (!builders.TryGetValue(id, out var builder))
            {
                builder = new SessionBuilder(
                    profile.Name,
                    id,
                    reader.IsDBNull(1) ? "unknown" : reader.GetString(1),
                    reader.IsDBNull(10) ? id : reader.GetString(10),
                    reader.IsDBNull(2) ? "unknown" : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    FromUnix(reader.GetDouble(4)),
                    reader.IsDBNull(5) ? null : FromUnix(reader.GetDouble(5)),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? 0 : reader.GetInt64(7),
                    reader.IsDBNull(8) ? 0 : reader.GetInt64(8),
                    reader.IsDBNull(9) ? 0 : reader.GetInt64(9));
                builders.Add(id, builder);
            }

            if (!reader.IsDBNull(11))
            {
                var input = GetInt64(reader, 16);
                var cacheRead = GetInt64(reader, 17);
                var cacheWrite = GetInt64(reader, 18);
                var output = GetInt64(reader, 19);
                var model = reader.GetString(11);
                var apiEquivalent = _pricing.Estimate(model, input, cacheRead, cacheWrite, output);
                builder.Usage.Add(new UsageLine(
                    model,
                    reader.IsDBNull(12) ? string.Empty : reader.GetString(12),
                    reader.IsDBNull(13) ? string.Empty : reader.GetString(13),
                    NormalizeTask(reader.IsDBNull(14) ? string.Empty : reader.GetString(14)),
                    GetInt64(reader, 15),
                    input,
                    cacheRead,
                    cacheWrite,
                    output,
                    GetInt64(reader, 20),
                    input + cacheRead + cacheWrite + output,
                    GetDouble(reader, 21),
                    GetDouble(reader, 22),
                    apiEquivalent is null ? null : (double)apiEquivalent.Value.CostUsd,
                    apiEquivalent?.Provider,
                    reader.IsDBNull(23) ? null : reader.GetString(23),
                    reader.IsDBNull(24) ? null : reader.GetString(24)));
            }
        }

        return new ProfileResult(totalSessions, builders.Values.Select(x => x.Build()).ToArray());
    }

    private static async Task<bool> HasTableAsync(SqliteConnection connection, string table, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name LIMIT 1";
        command.Parameters.AddWithValue("$name", table);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static DateTimeOffset FromUnix(double value) =>
        DateTimeOffset.FromUnixTimeMilliseconds((long)(value * 1000));

    private static long GetInt64(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? 0 : reader.GetInt64(ordinal);

    private static double GetDouble(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? 0 : reader.GetDouble(ordinal);

    private static string NormalizeTask(string task) => string.IsNullOrWhiteSpace(task) ? "agent" : task;

    private sealed record ProfileResult(int TotalSessions, IReadOnlyList<SessionMetrics> Sessions);

    private sealed class SessionBuilder(
        string profile,
        string id,
        string source,
        string title,
        string primaryModel,
        string? parentSessionId,
        DateTimeOffset startedAt,
        DateTimeOffset? endedAt,
        string? endReason,
        long messageCount,
        long toolCallCount,
        long sessionApiCalls)
    {
        public List<UsageLine> Usage { get; } = [];

        public SessionMetrics Build()
        {
            var lines = Usage.OrderByDescending(x => x.AccountedTokens).ThenBy(x => x.Model).ToArray();
            var status = endedAt is null ? "active" : string.IsNullOrWhiteSpace(endReason) ? "completed" : endReason;
            return new SessionMetrics(
                profile, id, source, title, primaryModel, parentSessionId,
                !string.IsNullOrWhiteSpace(parentSessionId), startedAt, endedAt,
                status, messageCount, toolCallCount,
                lines.Sum(x => x.AccountedTokens),
                lines.Sum(x => x.ReasoningTokens),
                lines.Length == 0 ? sessionApiCalls : lines.Sum(x => x.ApiCalls),
                lines.Sum(x => x.EstimatedCostUsd),
                lines.Sum(x => x.ActualCostUsd),
                lines.Where(x => x.ApiEquivalentCostUsd.HasValue).Sum(x => x.ApiEquivalentCostUsd!.Value),
                lines.Where(x => x.ApiEquivalentCostUsd.HasValue).Sum(x => x.AccountedTokens),
                lines.Where(x => !x.ApiEquivalentCostUsd.HasValue).Sum(x => x.AccountedTokens),
                lines);
        }
    }
}

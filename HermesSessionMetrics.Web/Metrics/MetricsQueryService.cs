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
        var parentIndexes = new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.Ordinal);

        foreach (var profile in profiles)
        {
            var result = await QueryProfileAsync(profile, cutoff, query.Search, cancellationToken);
            totalSessions += result.TotalSessions;
            sessions.AddRange(result.Sessions);
            parentIndexes[profile.Name] = result.ParentIndex;
        }

        var families = BuildFamilies(sessions, parentIndexes);
        var ordered = Order(families, query.Sort, query.Descending, query.CostBasis).ToArray();
        var filteredSessions = sessions.Count;
        var filteredFamilies = ordered.Length;
        var limit = Math.Clamp(query.Limit, 1, 1_000);
        var offset = Math.Max(query.Offset, 0);
        var page = ordered.Skip(offset).Take(limit).ToArray();

        watch.Stop();
        return new MetricsResult(
            2,
            now,
            cutoff,
            query.Hours,
            profiles.Select(x => x.Name).ToArray(),
            totalSessions,
            filteredSessions,
            filteredFamilies,
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

    private static IOrderedEnumerable<SessionFamilyMetrics> Order(
        IEnumerable<SessionFamilyMetrics> families,
        string sort,
        bool descending,
        string costBasis)
    {
        Func<SessionFamilyMetrics, IComparable> key = sort.ToLowerInvariant() switch
        {
            "started" => x => x.StartedAt,
            "cost" when costBasis.Equals("api-equivalent", StringComparison.OrdinalIgnoreCase) => x => x.ApiEquivalentCostUsd,
            "cost" => x => x.EstimatedCostUsd,
            "calls" => x => x.ApiCalls,
            _ => x => x.AccountedTokens
        };

        var ordered = descending
            ? families.OrderByDescending(key).ThenByDescending(x => x.StartedAt)
            : families.OrderBy(key).ThenBy(x => x.StartedAt);

        return ordered
            .ThenBy(x => x.Profile, StringComparer.Ordinal)
            .ThenBy(x => x.RootSessionId, StringComparer.Ordinal);
    }

    private static IReadOnlyList<SessionFamilyMetrics> BuildFamilies(
        IReadOnlyList<SessionMetrics> sessions,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>> parentIndexes)
    {
        var families = new List<SessionFamilyMetrics>();
        foreach (var profileSessions in sessions.GroupBy(x => x.Profile, StringComparer.Ordinal))
        {
            var parentIndex = parentIndexes[profileSessions.Key];
            var rootCache = new Dictionary<string, string>(StringComparer.Ordinal);
            var grouped = profileSessions.GroupBy(
                x => ResolveRootSessionId(x.Id, parentIndex, rootCache),
                StringComparer.Ordinal);
            foreach (var group in grouped)
            {
                var members = OrderFamilyMembers(group.Key, group.ToArray());
                var root = members.FirstOrDefault(x => x.Id == group.Key);
                families.Add(new SessionFamilyMetrics(
                    profileSessions.Key,
                    group.Key,
                    root is not null,
                    root?.StartedAt ?? members.Min(x => x.StartedAt),
                    members.Sum(x => x.AccountedTokens),
                    members.Sum(x => x.ReasoningTokens),
                    members.Sum(x => x.ApiCalls),
                    members.Sum(x => x.EstimatedCostUsd),
                    members.Sum(x => x.ActualCostUsd),
                    members.Sum(x => x.ApiEquivalentCostUsd),
                    members.Sum(x => x.ApiEquivalentPricedTokens),
                    members.Sum(x => x.ApiEquivalentUnpricedTokens),
                    members,
                    BuildFamilyUsageLines(members)));
            }
        }

        return families;
    }

    private static string ResolveRootSessionId(
        string sessionId,
        IReadOnlyDictionary<string, string?> parentIndex,
        IDictionary<string, string> cache)
    {
        if (cache.TryGetValue(sessionId, out var cachedRoot)) return cachedRoot;

        var path = new List<string>();
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        var currentId = sessionId;
        string root;

        while (true)
        {
            if (cache.TryGetValue(currentId, out var knownRoot))
            {
                root = knownRoot;
                break;
            }
            if (positions.TryGetValue(currentId, out var cycleStart))
            {
                root = path.Skip(cycleStart).Min(StringComparer.Ordinal)!;
                break;
            }

            positions[currentId] = path.Count;
            path.Add(currentId);
            if (!parentIndex.TryGetValue(currentId, out var parentId) || string.IsNullOrWhiteSpace(parentId))
            {
                root = currentId;
                break;
            }

            currentId = parentId;
        }

        foreach (var id in path) cache[id] = root;
        return root;
    }

    private static IReadOnlyList<SessionMetrics> OrderFamilyMembers(
        string rootSessionId,
        IReadOnlyList<SessionMetrics> members)
    {
        var byParent = members
            .Where(x => !string.IsNullOrWhiteSpace(x.ParentSessionId))
            .GroupBy(x => x.ParentSessionId!, StringComparer.Ordinal)
            .ToDictionary(
                x => x.Key,
                x => x.OrderBy(y => y.StartedAt).ThenBy(y => y.Id, StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);
        var byId = members.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var ordered = new List<SessionMetrics>(members.Count);
        var visited = new HashSet<string>(StringComparer.Ordinal);

        void Visit(SessionMetrics member)
        {
            if (!visited.Add(member.Id)) return;
            ordered.Add(member);
            if (byParent.TryGetValue(member.Id, out var children))
                foreach (var child in children) Visit(child);
        }

        if (byId.TryGetValue(rootSessionId, out var root)) Visit(root);
        else if (byParent.TryGetValue(rootSessionId, out var children))
            foreach (var child in children) Visit(child);

        foreach (var member in members.OrderBy(x => x.StartedAt).ThenBy(x => x.Id, StringComparer.Ordinal))
            Visit(member);

        return ordered;
    }

    private static IReadOnlyList<UsageLine> BuildFamilyUsageLines(IReadOnlyList<SessionMetrics> members)
    {
        var merged = MergeUsageLines(members.SelectMany(x => x.UsageLines));
        var residualCalls = Math.Max(0, members.Sum(x => x.ApiCalls) - merged.Sum(x => x.ApiCalls));
        if (residualCalls == 0) return merged;

        return MergeUsageLines(merged.Append(SessionTotalsUsageLine(residualCalls)));
    }

    private static UsageLine SessionTotalsUsageLine(long apiCalls) => new(
        "session totals",
        "unattributed",
        string.Empty,
        "unattributed",
        apiCalls,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        null,
        null,
        "session-only",
        "sessions.api_call_count");

    private static IReadOnlyList<UsageLine> MergeUsageLines(IEnumerable<UsageLine> usage) => usage
        .GroupBy(x => (x.Model, x.Task), ModelTaskComparer.Instance)
        .Select(MergeUsage)
        .OrderByDescending(x => x.AccountedTokens)
        .ThenBy(x => x.Model)
        .ThenBy(x => x.Task)
        .ToArray();

    private static UsageLine MergeUsage(IGrouping<(string Model, string Task), UsageLine> group)
    {
        var lines = group.ToArray();
        return new UsageLine(
            group.Key.Model,
            MergeLabels(lines.Select(x => x.Provider), includeUnattributed: true),
            MergeLabels(lines.Select(x => x.BillingMode), includeUnattributed: false),
            group.Key.Task,
            lines.Sum(x => x.ApiCalls),
            lines.Sum(x => x.InputTokens),
            lines.Sum(x => x.CacheReadTokens),
            lines.Sum(x => x.CacheWriteTokens),
            lines.Sum(x => x.OutputTokens),
            lines.Sum(x => x.ReasoningTokens),
            lines.Sum(x => x.AccountedTokens),
            lines.Sum(x => x.EstimatedCostUsd),
            lines.Sum(x => x.ActualCostUsd),
            lines.All(x => x.ApiEquivalentCostUsd.HasValue)
                ? lines.Sum(x => x.ApiEquivalentCostUsd!.Value)
                : null,
            MergeOptionalLabels(lines.Select(x => x.ApiEquivalentPricingProvider)),
            MergeOptionalLabels(lines.Select(x => x.CostStatus)),
            MergeOptionalLabels(lines.Select(x => x.CostSource)));
    }

    private static string MergeLabels(IEnumerable<string> values, bool includeUnattributed)
    {
        var labels = values
            .SelectMany(x => SplitLabels(
                string.IsNullOrWhiteSpace(x) ? (includeUnattributed ? "unattributed" : null) : x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x.Equals("unattributed", StringComparison.OrdinalIgnoreCase))
            .ThenBy(x => x, StringComparer.OrdinalIgnoreCase);
        return string.Join(", ", labels);
    }

    private static string? MergeOptionalLabels(IEnumerable<string?> values)
    {
        var merged = string.Join(", ", values
            .SelectMany(SplitLabels)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        return merged.Length == 0 ? null : merged;
    }

    private static IEnumerable<string> SplitLabels(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

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
            return new ProfileResult(0, [], new Dictionary<string, string?>());

        await using var transaction = connection.BeginTransaction(deferred: true);
        var parentIndex = new Dictionary<string, string?>(StringComparer.Ordinal);
        var parentCommand = connection.CreateCommand();
        parentCommand.Transaction = transaction;
        parentCommand.CommandText = "SELECT id, parent_session_id FROM sessions";
        await using (var parentReader = await parentCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await parentReader.ReadAsync(cancellationToken))
                parentIndex[parentReader.GetString(0)] = parentReader.IsDBNull(1) ? null : parentReader.GetString(1);
        }
        var totalSessions = parentIndex.Count;

        var command = connection.CreateCommand();
        command.Transaction = transaction;
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

        await transaction.CommitAsync(cancellationToken);
        return new ProfileResult(totalSessions, builders.Values.Select(x => x.Build()).ToArray(), parentIndex);
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

    private sealed record ProfileResult(
        int TotalSessions,
        IReadOnlyList<SessionMetrics> Sessions,
        IReadOnlyDictionary<string, string?> ParentIndex);

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
            var lines = MergeUsageLines(Usage);
            var residualCalls = Math.Max(0, sessionApiCalls - lines.Sum(x => x.ApiCalls));
            if (residualCalls > 0)
                lines = MergeUsageLines(lines.Append(SessionTotalsUsageLine(residualCalls)));
            var status = endedAt is null ? "active" : string.IsNullOrWhiteSpace(endReason) ? "completed" : endReason;
            return new SessionMetrics(
                profile, id, source, title, primaryModel, parentSessionId,
                !string.IsNullOrWhiteSpace(parentSessionId), startedAt, endedAt,
                status, messageCount, toolCallCount,
                lines.Sum(x => x.AccountedTokens),
                lines.Sum(x => x.ReasoningTokens),
                lines.Count == 0 ? sessionApiCalls : lines.Sum(x => x.ApiCalls),
                lines.Sum(x => x.EstimatedCostUsd),
                lines.Sum(x => x.ActualCostUsd),
                lines.Where(x => x.ApiEquivalentCostUsd.HasValue).Sum(x => x.ApiEquivalentCostUsd!.Value),
                lines.Where(x => x.ApiEquivalentCostUsd.HasValue).Sum(x => x.AccountedTokens),
                lines.Where(x => !x.ApiEquivalentCostUsd.HasValue).Sum(x => x.AccountedTokens),
                lines);
        }
    }

    private sealed class ModelTaskComparer : IEqualityComparer<(string Model, string Task)>
    {
        public static ModelTaskComparer Instance { get; } = new();

        public bool Equals((string Model, string Task) x, (string Model, string Task) y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.Model, y.Model) &&
            StringComparer.OrdinalIgnoreCase.Equals(x.Task, y.Task);

        public int GetHashCode((string Model, string Task) value) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Model),
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Task));
    }
}

using HermesSessionMetrics.Web.Metrics;
using Microsoft.Data.Sqlite;

namespace HermesSessionMetrics.Tests;

public sealed class ProfileCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"hermes-metrics-{Guid.NewGuid():N}");

    [Fact]
    public void Discover_finds_default_and_named_profiles_but_excludes_non_live_copies()
    {
        Touch("state.db");
        Touch("profiles/dev/state.db");
        Touch("profiles/personal/state.db");
        Touch("profiles/dev.quarantine-20260729/state.db");
        Touch("profiles/dev/mission-control/state.db");
        Touch("backups/dev-state.db");

        var profiles = new ProfileCatalog(_root).Discover();

        Assert.Equal(["default", "dev", "personal"], profiles.Select(x => x.Name));
        Assert.All(profiles, x => Assert.True(Path.IsPathFullyQualified(x.DatabasePath)));
    }

    private void Touch(string relativePath)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, []);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}

public sealed class MetricsQueryServiceTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"hermes-metrics-{Guid.NewGuid():N}.db");
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(2_000_000_000);

    [Fact]
    public async Task Query_returns_one_session_with_separate_model_and_task_lines()
    {
        await SeedAsync();
        var pricing = ApiPricingCatalog.FromEntries([
            new ApiPriceEntry("gpt-5.6-sol", "openai", 2m, 0.5m, 2m, 4m),
            new ApiPriceEntry("deepseek-v4-pro", "deepseek", 1m, 0.1m, 1m, 2m),
            new ApiPriceEntry("grok-4.5", "xai", 3m, 0.3m, 3m, 6m)
        ]);
        var service = new MetricsQueryService(pricing, () => Now);

        var result = await service.QueryAsync(
            [new ProfileDatabase("dev", _dbPath)],
            new MetricsQuery(24, null, 100, 0),
            CancellationToken.None);

        var session = Assert.Single(result.Sessions);
        Assert.Equal("dev", session.Profile);
        Assert.Equal("recent", session.Id);
        Assert.True(session.IsChild);
        Assert.Equal("parent-session", session.ParentSessionId);
        Assert.Equal(3, session.UsageLines.Count);
        Assert.Collection(session.UsageLines,
            x => { Assert.Equal("gpt-5.6-sol", x.Model); Assert.Equal("agent", x.Task); Assert.Equal(650, x.AccountedTokens); },
            x => { Assert.Equal("deepseek-v4-pro", x.Model); Assert.Equal("compression", x.Task); Assert.Equal(440, x.AccountedTokens); },
            x => { Assert.Equal("grok-4.5", x.Model); Assert.Equal("approval", x.Task); Assert.Equal(220, x.AccountedTokens); });
        Assert.Equal(1_310, session.AccountedTokens);
        Assert.Equal(0.0014, session.ApiEquivalentCostUsd, 9);
        Assert.Equal(1_310, result.ApiEquivalentPricedTokens);
        Assert.Equal(0, result.ApiEquivalentUnpricedTokens);
        Assert.Equal(2, result.TotalSessions);
        Assert.Equal(1, result.FilteredSessions);
    }

    [Fact]
    public async Task Query_filters_by_search_and_paginates_after_grouping()
    {
        await SeedAsync();
        var service = new MetricsQueryService(ApiPricingCatalog.Empty, () => Now);

        var result = await service.QueryAsync(
            [new ProfileDatabase("dev", _dbPath)],
            new MetricsQuery(24, "needle", 1, 0),
            CancellationToken.None);

        var session = Assert.Single(result.Sessions);
        Assert.Equal("recent", session.Id);
        Assert.Equal(1, result.FilteredSessions);
    }

    [Fact]
    public async Task Query_sorts_by_api_equivalent_cost_when_requested()
    {
        await SeedAsync();
        await using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = "UPDATE session_model_usage SET input_tokens=1000000, last_seen=$seen WHERE session_id='old'";
            command.Parameters.AddWithValue("$seen", Now.AddHours(-1).ToUnixTimeSeconds());
            await command.ExecuteNonQueryAsync();
        }
        var pricing = ApiPricingCatalog.FromEntries([
            new ApiPriceEntry("gpt-5.6-sol", "openai", 2m, 0.5m, 2m, 4m)
        ]);
        var service = new MetricsQueryService(pricing, () => Now);

        var result = await service.QueryAsync(
            [new ProfileDatabase("dev", _dbPath)],
            new MetricsQuery(24, null, 100, 0, "cost", true, "api-equivalent"),
            CancellationToken.None);

        Assert.Equal(["old", "recent"], result.Sessions.Select(x => x.Id));
    }

    private async Task SeedAsync()
    {
        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE sessions (
                id TEXT PRIMARY KEY, source TEXT NOT NULL, model TEXT,
                parent_session_id TEXT, started_at REAL NOT NULL, ended_at REAL,
                end_reason TEXT, message_count INTEGER DEFAULT 0,
                tool_call_count INTEGER DEFAULT 0, title TEXT,
                display_name TEXT, api_call_count INTEGER DEFAULT 0
            );
            CREATE TABLE session_model_usage (
                session_id TEXT NOT NULL, model TEXT NOT NULL,
                billing_provider TEXT NOT NULL DEFAULT '', billing_base_url TEXT NOT NULL DEFAULT '',
                billing_mode TEXT NOT NULL DEFAULT '', task TEXT NOT NULL DEFAULT '',
                api_call_count INTEGER NOT NULL DEFAULT 0, input_tokens INTEGER NOT NULL DEFAULT 0,
                output_tokens INTEGER NOT NULL DEFAULT 0, cache_read_tokens INTEGER NOT NULL DEFAULT 0,
                cache_write_tokens INTEGER NOT NULL DEFAULT 0, reasoning_tokens INTEGER NOT NULL DEFAULT 0,
                estimated_cost_usd REAL NOT NULL DEFAULT 0, actual_cost_usd REAL NOT NULL DEFAULT 0,
                cost_status TEXT, cost_source TEXT, first_seen REAL, last_seen REAL,
                PRIMARY KEY(session_id, model, billing_provider, billing_base_url, billing_mode, task)
            );
            """;
        await command.ExecuteNonQueryAsync();

        await InsertSession(connection, "recent", "needle session", Now.AddHours(-2).ToUnixTimeSeconds(), "parent-session");
        await InsertSession(connection, "old", "old session", Now.AddDays(-10).ToUnixTimeSeconds());
        await InsertUsage(connection, "recent", "gpt-5.6-sol", "openai-codex", "subscription_included", "", 100, 500, 0, 50, 10, 1, 0);
        await InsertUsage(connection, "recent", "deepseek-v4-pro", "deepseek", "", "compression", 200, 200, 0, 40, 20, 2, 0.08);
        await InsertUsage(connection, "recent", "grok-4.5", "xai-oauth", "", "approval", 100, 100, 0, 20, 0, 1, 0);
        await InsertUsage(connection, "old", "gpt-5.6-sol", "openai-codex", "subscription_included", "", 1, 1, 0, 1, 0, 1, 0);
        var ageOldUsage = connection.CreateCommand();
        ageOldUsage.CommandText = "UPDATE session_model_usage SET first_seen=$seen, last_seen=$seen WHERE session_id='old'";
        ageOldUsage.Parameters.AddWithValue("$seen", Now.AddDays(-10).ToUnixTimeSeconds());
        await ageOldUsage.ExecuteNonQueryAsync();
    }

    private static async Task InsertSession(SqliteConnection connection, string id, string title, long started, string? parentSessionId = null)
    {
        var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO sessions(id,source,model,parent_session_id,started_at,title) VALUES($id,'discord','gpt-5.6-sol',$parent,$started,$title)";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$parent", (object?)parentSessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$started", started);
        command.Parameters.AddWithValue("$title", title);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertUsage(SqliteConnection connection, string session, string model, string provider, string billingMode, string task, long input, long cacheRead, long cacheWrite, long output, long reasoning, long calls, double cost)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO session_model_usage(session_id,model,billing_provider,billing_mode,task,api_call_count,input_tokens,cache_read_tokens,cache_write_tokens,output_tokens,reasoning_tokens,estimated_cost_usd,first_seen,last_seen)
            VALUES($session,$model,$provider,$billing,$task,$calls,$input,$cacheRead,$cacheWrite,$output,$reasoning,$cost,$seen,$seen)
            """;
        command.Parameters.AddWithValue("$session", session);
        command.Parameters.AddWithValue("$model", model);
        command.Parameters.AddWithValue("$provider", provider);
        command.Parameters.AddWithValue("$billing", billingMode);
        command.Parameters.AddWithValue("$task", task);
        command.Parameters.AddWithValue("$calls", calls);
        command.Parameters.AddWithValue("$input", input);
        command.Parameters.AddWithValue("$cacheRead", cacheRead);
        command.Parameters.AddWithValue("$cacheWrite", cacheWrite);
        command.Parameters.AddWithValue("$output", output);
        command.Parameters.AddWithValue("$reasoning", reasoning);
        command.Parameters.AddWithValue("$cost", cost);
        command.Parameters.AddWithValue("$seen", Now.AddHours(-1).ToUnixTimeSeconds());
        await command.ExecuteNonQueryAsync();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }
}

public sealed class ApiPricingCatalogTests
{
    [Fact]
    public void Estimate_uses_separate_input_cache_and_output_rates_per_million_tokens()
    {
        var catalog = ApiPricingCatalog.FromEntries([
            new ApiPriceEntry("gpt-test", "openai", 2m, 0.5m, 3m, 4m)
        ]);

        var estimate = catalog.Estimate("GPT-TEST", 1_000_000, 2_000_000, 3_000_000, 4_000_000);

        Assert.NotNull(estimate);
        Assert.Equal(28m, estimate.Value.CostUsd);
        Assert.Equal("openai", estimate.Value.Provider);
    }

    [Fact]
    public void Estimate_returns_null_when_the_model_has_no_local_price()
    {
        Assert.Null(ApiPricingCatalog.Empty.Estimate("unknown", 1, 2, 3, 4));
    }

    [Fact]
    public void Load_exposes_all_pricing_sources_for_auditability()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """
                {
                  "schemaVersion": 2,
                  "source": {
                    "name": "Official provider pricing with models.dev fallback",
                    "url": "https://example.test/pricing",
                    "retrievedAt": "2026-07-31T09:18:47Z"
                  },
                  "sources": [
                    {
                      "id": "openai-pricing",
                      "name": "OpenAI API pricing",
                      "url": "https://developers.openai.com/api/docs/pricing.md",
                      "retrievedAt": "2026-07-31T09:18:47Z",
                      "provider": "openai",
                      "basis": "standard short-context rates"
                    },
                    {
                      "id": "models-dev",
                      "name": "models.dev fallback",
                      "url": "https://models.dev/api.json",
                      "retrievedAt": "2026-07-31T09:18:47Z"
                    }
                  ],
                  "models": [
                    {
                      "model": "gpt-test",
                      "provider": "openai",
                      "inputPerMillion": 1,
                      "cacheReadPerMillion": 0.1,
                      "cacheWritePerMillion": 1.25,
                      "outputPerMillion": 6,
                      "sourceIds": ["openai-pricing"],
                      "basis": "standard short-context rates"
                    }
                  ]
                }
                """);

            var catalog = ApiPricingCatalog.Load(path);

            Assert.Collection(catalog.Sources,
                source =>
                {
                    Assert.Equal("openai", source.Provider);
                    Assert.Equal("standard short-context rates", source.Basis);
                },
                source => Assert.Null(source.Provider));
            var entry = Assert.Single(catalog.Entries);
            Assert.NotNull(entry.SourceIds);
            Assert.Equal("openai-pricing", Assert.Single(entry.SourceIds));
            Assert.Equal("standard short-context rates", entry.Basis);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_rejects_schema_two_models_without_provenance()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """
                {
                  "schemaVersion": 2,
                  "source": {
                    "name": "merged",
                    "url": "data/api-pricing.json",
                    "retrievedAt": "2026-07-31T09:18:47Z"
                  },
                  "sources": [
                    {
                      "id": "official",
                      "name": "official",
                      "url": "https://example.test/pricing",
                      "retrievedAt": "2026-07-31T09:18:47Z"
                    }
                  ],
                  "models": [
                    {
                      "model": "missing-provenance",
                      "provider": "test",
                      "inputPerMillion": 1,
                      "cacheReadPerMillion": 1,
                      "cacheWritePerMillion": 1,
                      "outputPerMillion": 1
                    }
                  ]
                }
                """);

            Assert.Throws<InvalidDataException>(() => ApiPricingCatalog.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

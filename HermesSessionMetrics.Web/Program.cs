using HermesSessionMetrics.Web.Metrics;
using Microsoft.AspNetCore.Http.Json;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<JsonOptions>(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.WriteIndented = false;
});
builder.Services.AddSingleton(_ =>
{
    var configured = builder.Configuration["HermesRoot"];
    var root = string.IsNullOrWhiteSpace(configured)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".hermes")
        : configured;
    return new ProfileCatalog(root);
});
builder.Services.AddSingleton(_ =>
{
    var configured = builder.Configuration["PricingTablePath"];
    var path = string.IsNullOrWhiteSpace(configured)
        ? Path.Combine(AppContext.BaseDirectory, "data", "api-pricing.json")
        : configured;
    return ApiPricingCatalog.Load(path);
});
builder.Services.AddSingleton<MetricsQueryService>();

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        context.Context.Response.Headers.CacheControl = "no-cache";
        context.Context.Response.Headers.XContentTypeOptions = "nosniff";
    }
});

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", time = DateTimeOffset.UtcNow }));

app.MapGet("/api/profiles", (ProfileCatalog catalog) =>
{
    var profiles = catalog.Discover().Select(x => new
    {
        x.Name,
        sizeBytes = new FileInfo(x.DatabasePath).Length
    });
    return Results.Ok(profiles);
});

app.MapGet("/api/pricing", (ApiPricingCatalog pricing) => Results.Ok(new
{
    pricing.Source,
    pricing.Sources,
    modelCount = pricing.Count
}));

app.MapGet("/api/metrics", async (
    string? profiles,
    string? window,
    string? search,
    string? sort,
    string? costBasis,
    bool? descending,
    int? limit,
    int? offset,
    ProfileCatalog catalog,
    MetricsQueryService service,
    CancellationToken cancellationToken) =>
{
    var available = catalog.Discover();
    var defaultProfile = available.FirstOrDefault()?.Name ?? string.Empty;
    var requestedNames = (profiles ?? defaultProfile)
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    var selected = requestedNames.Contains("all")
        ? available
        : available.Where(x => requestedNames.Contains(x.Name)).ToArray();

    if (selected.Count == 0)
        return Results.BadRequest(new { error = "No valid profiles selected." });

    var hours = window?.ToLowerInvariant() switch
    {
        "7d" => 168,
        "30d" => 720,
        "24h" or null or "" => 24,
        _ => 0
    };
    if (hours == 0)
        return Results.BadRequest(new { error = "Window must be 24h, 7d, or 30d." });

    var query = new MetricsQuery(
        hours,
        search,
        limit ?? 250,
        offset ?? 0,
        sort ?? "tokens",
        descending ?? true,
        costBasis ?? "recorded");
    var result = await service.QueryAsync(selected, query, cancellationToken);
    return Results.Ok(result);
});

app.MapFallbackToFile("index.html");
app.Run();

public partial class Program;

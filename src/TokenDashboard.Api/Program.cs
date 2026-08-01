using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Net;
using System.Globalization;
using System.Reflection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using TokenDashboard.Data;

namespace TokenDashboard.Api;

public static class ProgramEntry
{
    private static readonly string[] ClearTables = ["session_tags", "source_tags", "project_tags", "token_usages", "turn_usage_tokens", "turn_usage_facts", "session_usage_rollups", "daily_usage_rollups", "contents", "sub_events", "turns", "sessions", "imports", "source_file_manifest", "import_jobs", "tags", "sources", "search_index"];

    public static WebApplication BuildApplication(string[] args)
    {
        var webRootPath = ResolveWebRootPath();
        var hasWebRoot = webRootPath is not null;
        var builder = webRootPath is null
            ? WebApplication.CreateBuilder(args)
            : WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = args,
                WebRootPath = webRootPath
            });
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        var listenPort = builder.Configuration.GetValue("TokenDashboard:ListenPort", 0);
        if (listenPort is < 0 or > 65535)
        {
            throw new InvalidOperationException("Listen port must be between 0 and 65535");
        }

        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, listenPort));
        var maxImportBytes = builder.Configuration.GetValue<long?>("TokenDashboard:MaxImportBytes") ?? ApiOptions.DefaultMaxImportBytes;
        if (maxImportBytes > 0)
        {
            builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = maxImportBytes);
        }
        builder.Services.AddOptions<ApiOptions>().BindConfiguration("TokenDashboard");
        builder.Services.AddSingleton<SessionKeyService>();
        builder.Services.AddSingleton<IBrowserLauncher, ProcessBrowserLauncher>();
        builder.Services.AddSingleton<DashboardStore>();
        builder.Services.AddSingleton<DashboardDataService>();
        builder.Services.AddSingleton<SourceManagementService>();
        builder.Services.AddSingleton<SourceManifestService>();
        builder.Services.AddSingleton<DashboardReadService>();
        builder.Services.AddSingleton<BudgetService>();
        builder.Services.AddSingleton<PricingResolver>();
        builder.Services.AddSingleton<SourceAdapterRegistry>();
        builder.Services.AddSingleton<SyncJobService>();
        builder.Services.AddSingleton<PricingService>();
        builder.Services.AddHostedService<BrowserStartupService>();
        builder.Services.AddHostedService<StartupDataService>();
        builder.Services.AddHostedService<SyncWorker>();

        var app = builder.Build();
        app.UseMiddleware<StartupEntryRedirectMiddleware>();
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.UseMiddleware<LoopbackCorsMiddleware>();
        app.UseMiddleware<SessionKeyMiddleware>();

        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

        app.MapPost("/api/sync", ([FromBody] SyncRequest request, [FromServices] SyncJobService jobs) =>
        {
            if (!jobs.TryEnqueue(request, out var syncId, out var active))
            {
                return Results.Conflict(new { error = "A data job is already running", activeJob = active });
            }

            return Results.Accepted($"/api/sync/{syncId}", new { syncId, status = "queued" });
        });

        app.MapGet("/api/sync/{syncId:guid}", (Guid syncId, SyncJobService jobs) =>
            jobs.TryGet(syncId, out var status) ? Results.Ok(status) : Results.NotFound());
        app.MapGet("/api/import-jobs/active", (SyncJobService jobs) =>
            jobs.TryGetActive(out var status) ? Results.Ok(status) : Results.NoContent());

        app.MapGet("/api/overview", (HttpRequest request, DashboardReadService dashboard) => Results.Ok(dashboard.Overview(Range(request), Filter(request))));
        app.MapGet("/api/usage/daily", (HttpRequest request, DashboardReadService dashboard) => Results.Ok(dashboard.Daily(Range(request), Filter(request))));
        app.MapGet("/api/usage/trend", (HttpRequest request, DashboardReadService dashboard) =>
        {
            return dashboard.TryTrend(Range(request), request.Query["interval"].ToString(), Filter(request), out var points)
                ? Results.Ok(points)
                : Results.BadRequest(new { error = "interval must be one of 15m, 30m, 1h, 3h, 6h, 1d, 3d or 7d" });
        });
        app.MapGet("/api/usage/monthly", (HttpRequest request, DashboardReadService dashboard) => Results.Ok(dashboard.Monthly(Range(request), Filter(request))));
        app.MapGet("/api/heatmap", (HttpRequest request, DashboardReadService dashboard) => Results.Ok(dashboard.Heatmap(Range(request), Filter(request))));
        app.MapGet("/api/comparisons", (HttpRequest request, DashboardReadService dashboard) => Results.Ok(dashboard.Comparisons(Range(request), request.Query["groupBy"].ToString(), Filter(request))));
        app.MapGet("/api/comparisons/tree", (HttpRequest request, DashboardReadService dashboard) => Results.Ok(dashboard.ComparisonTree(Range(request), Filter(request))));
        app.MapGet("/api/dashboard-snapshot", (HttpRequest request, DashboardReadService dashboard) =>
        {
            var pageSize = ParsePositive(request.Query["pageSize"].ToString(), 50);
            return Results.Ok(dashboard.Snapshot(Range(request), Filter(request), request.Query["cursor"].ToString(), pageSize));
        });
        app.MapGet("/api/sessions", (HttpRequest request, DashboardReadService dashboard) => Results.Ok(dashboard.Sessions(Range(request), Filter(request))));
        app.MapGet("/api/sessions/page", (HttpRequest request, DashboardReadService dashboard) =>
        {
            var pageSize = ParsePositive(request.Query["pageSize"].ToString(), 50);
            return Results.Ok(dashboard.SessionsPage(Range(request), Filter(request), request.Query["cursor"].ToString(), pageSize));
        });
        app.MapGet("/api/sessions/{sessionId}/timeline", (HttpRequest request, string sessionId, DashboardReadService dashboard) =>
        {
            var pageSize = ParsePositive(request.Query["pageSize"].ToString(), 100);
            var reveal = string.Equals(request.Query["reveal"], "true", StringComparison.OrdinalIgnoreCase);
            return dashboard.SessionTimeline(sessionId, request.Query["cursor"].ToString(), pageSize, reveal) is { } timeline
                ? Results.Ok(timeline)
                : Results.NotFound();
        });
        app.MapGet("/api/sessions/{sessionId}", (HttpRequest request, string sessionId, DashboardReadService dashboard) =>
            dashboard.Session(sessionId, string.Equals(request.Query["reveal"], "true", StringComparison.OrdinalIgnoreCase) || string.Equals(request.Query["includeContent"], "true", StringComparison.OrdinalIgnoreCase) || string.Equals(request.Query["showContent"], "true", StringComparison.OrdinalIgnoreCase)) is { } session
                ? Results.Ok(session)
                : Results.NotFound());
        app.MapGet("/api/sessions/{sessionId}/events/{fingerprint}/{field}", (string sessionId, string fingerprint, string field, DashboardReadService dashboard) =>
            dashboard.EventContent(sessionId, fingerprint, field) is { } content
                ? Results.Ok(content)
                : Results.NotFound());

        app.MapGet("/api/search", (HttpRequest request, DashboardReadService dashboard) =>
        {
            var query = request.Query["q"].ToString();
            if (string.IsNullOrWhiteSpace(query))
            {
                return Results.BadRequest(new { error = "q is required" });
            }

            var page = ParsePositive(request.Query["page"].ToString(), 1);
            var pageSize = Math.Min(ParsePositive(request.Query["pageSize"].ToString(), 50), 500);
            var sourceId = request.Query["sourceId"].ToString();
            return Results.Ok(new { page, pageSize, query, results = dashboard.Search(query, page, pageSize, string.IsNullOrWhiteSpace(sourceId) ? null : sourceId) });
        });

        app.MapGet("/api/tags", (DashboardDataService data) => Results.Ok(TagAssignments(data)));
        app.MapPost("/api/tags", ([FromBody] TagRequest request, [FromServices] DashboardDataService data) => AddTag(request, data));
        app.MapDelete("/api/tags/{scope}/{entityId}/{tagId}", (HttpRequest request, string scope, string entityId, string tagId, DashboardDataService data) => RemoveTag(request, scope, entityId, tagId, data));

        app.MapGet("/api/budgets", (BudgetService budgets) => Results.Ok(budgets.List()));
        app.MapPost("/api/budgets", ([FromBody] BudgetRequest request, BudgetService budgets) =>
        {
            if (!ValidateBudget(request, out var error)) return Results.BadRequest(new { error });
            var budget = budgets.Create(request);
            return Results.Created($"/api/budgets/{budget.Id}", budget);
        });
        app.MapPut("/api/budgets/{id}", (string id, [FromBody] BudgetRequest request, BudgetService budgets) =>
        {
            if (!ValidateBudget(request, out var error)) return Results.BadRequest(new { error });
            return budgets.Update(id, request) is { } budget ? Results.Ok(budget) : Results.NotFound();
        });
        app.MapDelete("/api/budgets/{id}", (string id, BudgetService budgets) => budgets.Delete(id) ? Results.NoContent() : Results.NotFound());
        app.MapGet("/api/budgets/summary", (HttpRequest request, BudgetService budgets, DashboardReadService dashboard) => Results.Ok(BudgetSummaries(budgets.List(), dashboard)));
        app.MapGet("/api/budgets/{id}/summary", (string id, BudgetService budgets, DashboardReadService dashboard) => budgets.Get(id) is { } budget ? Results.Ok(BudgetSummaries([budget], dashboard).Single()) : Results.NotFound());

        app.MapGet("/api/pricing", (PricingService pricing) => Results.Ok(new { currency = "USD", catalogVersion = BuiltInPricingCatalog.Version, overrideCount = pricing.OverrideCount, entries = pricing.List() }));
        app.MapGet("/api/pricing/unknown", (HttpRequest request, DashboardReadService dashboard) => Results.Ok(dashboard.UnknownPricing(Range(request), Filter(request))));
        app.MapPut("/api/pricing", ([FromBody] PriceWriteRequest request, [FromServices] PricingService pricing) =>
        {
            var effectiveFrom = (request.EffectiveFromUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
            var effectiveTo = request.EffectiveToUtc?.ToUniversalTime();
            if (request.UsdPerMillionTokens < 0 || request.MinimumInputTokens < 0 || request.MaximumInputTokens is { } maximum && maximum <= request.MinimumInputTokens || effectiveTo is { } end && end <= effectiveFrom)
            {
                return Results.BadRequest(new { error = "Invalid price threshold or amount" });
            }

            return Results.Ok(pricing.Add(request with { EffectiveFromUtc = effectiveFrom, EffectiveToUtc = effectiveTo }));
        });
        app.MapPost("/api/pricing/deactivate", ([FromBody] PriceDeactivateRequest request, PricingService pricing) =>
        {
            if (string.IsNullOrWhiteSpace(request.Provider) || string.IsNullOrWhiteSpace(request.Model) || string.IsNullOrWhiteSpace(request.TokenType))
            {
                return Results.BadRequest(new { error = "provider, model and tokenType are required" });
            }

            return Results.Ok(new { deactivated = pricing.Deactivate(request) });
        });

        app.MapGet("/api/sources/capabilities", (SourceAdapterRegistry registry) => Results.Ok(registry.All.Select(item => item.GetCapabilities())));
        app.MapGet("/api/sources/managed", (SourceManagementService sources) => Results.Ok(sources.List()));
        app.MapPut("/api/sources/managed", ([FromBody] ManagedSourceRequest request, SourceManagementService sources) =>
        {
            try
            {
                return Results.Ok(sources.Upsert(request));
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });
        app.MapPost("/api/sources/managed/{id}/enable", (string id, SourceManagementService sources) => sources.SetEnabled(id, true) ? Results.NoContent() : Results.NotFound());
        app.MapPost("/api/sources/managed/{id}/disable", (string id, SourceManagementService sources) => sources.SetEnabled(id, false) ? Results.NoContent() : Results.NotFound());
        app.MapDelete("/api/sources/managed/{id}", (string id, SourceManagementService sources) => sources.Delete(id) ? Results.NoContent() : Results.NotFound());
        app.MapGet("/api/sources/discovery", (HttpRequest request, SourceAdapterRegistry registry, Microsoft.Extensions.Options.IOptions<ApiOptions> options) =>
        {
            try
            {
                var custom = request.Query["path"].Where(static item => !string.IsNullOrWhiteSpace(item)).Select(static item => item!).ToArray();
                var discovery = new SourceDiscoveryOptions(SourceAdapterRegistry.CurrentPlatform, options.Value.SourceHome ?? SourceAdapterRegistry.UserHome, options.Value.SourceAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), custom);
                if (string.Equals(request.Query["adapter"].ToString(), "auto", StringComparison.OrdinalIgnoreCase))
                {
                    return Results.Ok(registry.All.Select(adapter => new { adapter = adapter.Kind.ToString(), capabilities = adapter.GetCapabilities(), paths = adapter.DiscoverPaths(discovery) }).ToArray());
                }

                var adapter = registry.Get(request.Query["adapter"].ToString());
                return Results.Ok(new { adapter = adapter.Kind.ToString(), capabilities = adapter.GetCapabilities(), paths = adapter.DiscoverPaths(discovery) });
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });
        app.MapPost("/api/sources/preview", ([FromBody] SourcePreviewRequest request, SourceAdapterRegistry registry) => PreviewSource(request, registry));
        app.MapPost("/api/sources/import", ([FromBody] SourceImportRequest request, [FromServices] SourceAdapterRegistry registry, [FromServices] DashboardDataService data, [FromServices] SourceManagementService sources, [FromServices] SyncJobService jobs, [FromServices] IOptions<ApiOptions> options) => ImportSource(request, registry, data, sources, jobs, options.Value.MaxImportBytes));

        app.MapPost("/api/export", ([FromBody] ExportRequest request, [FromServices] DashboardReadService dashboard, [FromServices] DashboardDataService data, HttpResponse response) => Export(request, dashboard, data, response));
        app.MapDelete("/api/data", ([FromBody] DeleteDataRequest request, [FromServices] DashboardDataService data) => Delete(request, data));
        if (!hasWebRoot)
        {
            app.MapGet("/", () => Results.Content("<!doctype html><html><body><div id=\"app\"></div></body></html>", "text/html"));
        }
        app.MapFallbackToFile("index.html");

        return app;
    }

    public static void Main(string[] args)
    {
        if (TryWriteVersion(args))
        {
            return;
        }

        BuildApplication(args).Run();
    }

    private static bool TryWriteVersion(string[] args)
    {
        if (!args.Any(static argument => string.Equals(argument, "--version", StringComparison.Ordinal) || string.Equals(argument, "-v", StringComparison.Ordinal)))
        {
            return false;
        }

        var version = typeof(ProgramEntry).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        Console.WriteLine(string.IsNullOrWhiteSpace(version) ? "0.0.0-development" : version);
        return true;
    }

    private static DateRange Range(HttpRequest request) => DateRangeResolver.Resolve(
        request.Query["preset"].ToString(),
        request.Query["from"].ToString(),
        request.Query["to"].ToString(),
        request.Query["timeZone"].ToString());

    private static DashboardFilter Filter(HttpRequest request) => new(
        NullIfEmpty(request.Query["sourceId"].ToString()),
        NullIfEmpty(request.Query["tool"].ToString()),
        NullIfEmpty(request.Query["model"].ToString()),
        NullIfEmpty(request.Query["tokenType"].ToString()),
        NullIfEmpty(request.Query["workspaceId"].ToString()),
        NullIfEmpty(request.Query["projectId"].ToString()),
        NullIfEmpty(request.Query["tag"].ToString()));

    private static bool ValidateBudget(BudgetRequest request, out string error)
    {
        error = string.Empty;
        var period = request.Period?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(request.Name) || request.AmountUsd < 0 || period is not ("daily" or "monthly" or "custom") || period == "custom" && string.IsNullOrWhiteSpace(request.ToDate))
        {
            error = "name, non-negative amountUsd and period (daily, monthly or custom) are required";
            return false;
        }

        if (!DateTime.TryParse(request.FromDate ?? DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, DateTimeStyles.None, out var from) || request.ToDate is not null && (!DateTime.TryParse(request.ToDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var to) || to.Date < from.Date))
        {
            error = "fromDate and toDate must be valid dates with toDate on or after fromDate";
            return false;
        }

        return true;
    }

    private static BudgetSummaryDto[] BudgetSummaries(IReadOnlyList<BudgetDto> budgets, DashboardReadService dashboard)
    {
        return budgets.Select(budget =>
        {
            var from = DateTime.Parse(budget.FromDate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal);
            var to = budget.ToDate is not null ? DateTime.Parse(budget.ToDate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal).AddDays(1) : budget.Period switch
            {
                "daily" => from.Date.AddDays(1),
                "monthly" => new DateTime(from.Year, from.Month, 1).AddMonths(1),
                _ => from.Date.AddDays(1)
            };
            var range = DateRangeResolver.Resolve(null, from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), to.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "UTC");
            var overview = dashboard.Overview(range, new DashboardFilter(ProjectId: budget.ProjectId, Tag: budget.Tag));
            var json = JsonSerializer.SerializeToElement(overview);
            var spent = json.GetProperty("costUsd");
            var partial = json.GetProperty("partialCostUsd").GetDecimal();
            var spentUsd = spent.ValueKind == JsonValueKind.Null ? partial : spent.GetDecimal();
            var tokens = json.GetProperty("totalTokens").GetInt64();
            decimal? coverage = json.GetProperty("costCoverage").ValueKind == JsonValueKind.Null ? null : json.GetProperty("costCoverage").GetDecimal();
            return new BudgetSummaryDto(budget.Id, spentUsd, tokens, coverage, budget.AmountUsd == 0 ? 0 : spentUsd / budget.AmountUsd * 100m);
        }).ToArray();
    }

    private static string? ResolveWebRootPath()
    {
        var currentDirectory = Directory.GetCurrentDirectory();
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "wwwroot"),
            Path.Combine(currentDirectory, "src", "TokenDashboard.Web", "dist"),
            Path.GetFullPath(Path.Combine(currentDirectory, "..", "TokenDashboard.Web", "dist")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "TokenDashboard.Web", "dist"))
        };
        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(path => File.Exists(Path.Combine(path, "index.html")));
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static int ParsePositive(string value, int fallback) => int.TryParse(value, out var result) && result > 0 ? result : fallback;

    private static IResult AddTag(TagRequest request, DashboardDataService data)
    {
        var scope = request.Scope?.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(scope) || string.IsNullOrWhiteSpace(request.EntityId) || string.IsNullOrWhiteSpace(request.Key) || scope is not ("source" or "session" or "project"))
        {
            return Results.BadRequest(new { error = "scope, entityId and key are required" });
        }

        var tagId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{request.Key}\n{request.Value}"))).ToLowerInvariant();
        data.Transaction((connection, transaction) =>
        {
            Execute(connection, transaction, "INSERT OR IGNORE INTO tags (tag_id, tag_key, tag_value, created_at_utc) VALUES ($id, $key, $value, $now);", ("$id", tagId), ("$key", request.Key), ("$value", request.Value), ("$now", DateTimeOffset.UtcNow.ToString("O")));
            if (scope == "session")
            {
                Execute(connection, transaction, "INSERT OR IGNORE INTO session_tags (session_id, tag_id) VALUES ($entityId, $tagId);", ("$entityId", request.EntityId), ("$tagId", tagId));
            }
            else if (scope == "project")
            {
                Execute(connection, transaction, "INSERT OR IGNORE INTO project_tags (project_id, tag_id) VALUES ($entityId, $tagId);", ("$entityId", request.EntityId), ("$tagId", tagId));
            }
            else
            {
                Execute(connection, transaction, "INSERT OR IGNORE INTO source_tags (source_id, tag_id) VALUES ($entityId, $tagId);", ("$entityId", request.EntityId), ("$tagId", tagId));
            }
        });
        data.RebuildFts();
        return Results.Ok(new { id = tagId, scope, request.EntityId, request.Key, request.Value });
    }

    private static object[] TagAssignments(DashboardDataService data)
    {
        return data.Query("""
            SELECT 'source' AS scope, source_tags.source_id AS entity_id, t.tag_id AS id, t.tag_key AS key, t.tag_value AS value, t.created_at_utc AS created_at_utc
            FROM source_tags INNER JOIN tags AS t ON t.tag_id = source_tags.tag_id
            UNION ALL
            SELECT 'session', session_tags.session_id, t.tag_id, t.tag_key, t.tag_value, t.created_at_utc
            FROM session_tags INNER JOIN tags AS t ON t.tag_id = session_tags.tag_id
            UNION ALL
            SELECT 'project', project_tags.project_id, t.tag_id, t.tag_key, t.tag_value, t.created_at_utc
            FROM project_tags INNER JOIN tags AS t ON t.tag_id = project_tags.tag_id
            ORDER BY key, value, scope, entity_id;
            """).Select(row => (object)new
        {
            scope = row["scope"]?.ToString() ?? string.Empty,
            entityId = row["entity_id"]?.ToString() ?? string.Empty,
            id = row["id"]?.ToString() ?? string.Empty,
            key = row["key"]?.ToString() ?? string.Empty,
            value = row["value"]?.ToString() ?? string.Empty,
            createdAtUtc = row["created_at_utc"]?.ToString() ?? string.Empty
        }).ToArray();
    }

    private static IResult PreviewSource(SourcePreviewRequest request, SourceAdapterRegistry registry)
    {
        if (string.IsNullOrWhiteSpace(request.Path))
        {
            return Results.BadRequest(new { error = "path is required" });
        }

        var path = Path.GetFullPath(request.Path.Trim());
        var files = EnumeratePreviewFiles(path).Take(20).ToArray();
        if (files.Length == 0 && !File.Exists(path) && !Directory.Exists(path))
        {
            return Results.NotFound(new { error = "Source path was not found" });
        }

        var explicitAdapter = string.Equals(request.Adapter, "auto", StringComparison.OrdinalIgnoreCase) ? null : registry.Get(request.Adapter);
        var suggested = explicitAdapter?.Kind.ToString() ?? registry.IdentifyAutoPath(path)?.Kind.ToString();
        if (suggested is null)
        {
            suggested = SuggestAdapterFromFiles(files);
        }

        var totalBytes = files.Sum(file => new FileInfo(file).Length);
        return Results.Ok(new
        {
            path,
            requestedAdapter = request.Adapter,
            suggestedAdapter = suggested,
            sampleLimit = 20,
            sampledFileCount = files.Length,
            sampledFiles = files.Select(Path.GetFileName).ToArray(),
            sampledBytes = totalBytes,
            requiresConfirmation = explicitAdapter is null,
            canImport = suggested is not null
        });
    }

    private static List<string> EnumeratePreviewFiles(string path)
    {
        if (File.Exists(path))
        {
            return IsSupportedSourceFile(path) ? [path] : [];
        }

        if (!Directory.Exists(path)) return [];
        var files = new List<string>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories))
            {
                if (!IsSupportedSourceFile(file)) continue;
                files.Add(file);
                if (files.Count >= 20) break;
            }
        }
        catch (UnauthorizedAccessException)
        {
        }

        return files;
    }

    private static bool IsSupportedSourceFile(string path) => Path.GetExtension(path).ToLowerInvariant() is ".json" or ".jsonl" or ".ndjson" or ".csv";

    private static string? SuggestAdapterFromFiles(IEnumerable<string> files)
    {
        var names = string.Join(" ", files.Select(Path.GetFileName)).ToLowerInvariant();
        if (names.Contains("claude") && names.Contains("app")) return SourceAdapterKind.ClaudeCodeApp.ToString();
        if (names.Contains("claude")) return SourceAdapterKind.ClaudeCodeCli.ToString();
        if (names.Contains("codex") && names.Contains("app")) return SourceAdapterKind.CodexApp.ToString();
        if (names.Contains("codex")) return SourceAdapterKind.CodexCli.ToString();
        return null;
    }

    private static IResult ImportSource(SourceImportRequest request, SourceAdapterRegistry registry, DashboardDataService data, SourceManagementService sources, SyncJobService jobs, long maxImportBytes)
    {
        if (!jobs.TryAcquireInline())
        {
            return Results.Conflict(new { error = "A data job is already running" });
        }

        try
        {
            var adapter = registry.Get(request.Adapter);
            if (request.Content is not null)
            {
                if (string.IsNullOrWhiteSpace(request.FileName) || !TryGetImportExtension(request.FileName, out var extension))
                {
                    return Results.BadRequest(new { error = "fileName must use .json, .jsonl, .ndjson or .csv" });
                }

                if (maxImportBytes <= 0 || Encoding.UTF8.GetByteCount(request.Content) > maxImportBytes)
                {
                    return Results.BadRequest(new { error = $"Import content exceeds the configured {maxImportBytes / (1024 * 1024)} MiB limit" });
                }

                var temporaryPath = Path.Combine(Path.GetTempPath(), $"token-dashboard-import-{Guid.NewGuid():N}{extension}");
                try
                {
                    File.WriteAllText(temporaryPath, request.Content, Encoding.UTF8);
                    if (Encoding.UTF8.GetByteCount(request.Content) <= 8L * 1024 * 1024)
                    {
                        var inlineSummary = data.Import(Guid.NewGuid().ToString("N"), temporaryPath, adapter, request.WorkspaceId, request.OwnerId);
                        File.Delete(temporaryPath);
                        return Results.Ok(inlineSummary);
                    }

                    if (!jobs.TryEnqueue(new SyncRequest(request.Adapter, [temporaryPath], request.WorkspaceId, request.OwnerId, CleanupPathsAfterCompletion: true), out var syncId, out var active))
                    {
                        File.Delete(temporaryPath);
                        return Results.Conflict(new { error = "A data job is already running", activeJob = active });
                    }

                    return Results.Accepted($"/api/sync/{syncId}", new { syncId, status = "queued" });
                }
                catch
                {
                    if (File.Exists(temporaryPath))
                    {
                        File.Delete(temporaryPath);
                    }

                    throw;
                }
            }

            if (string.IsNullOrWhiteSpace(request.Path) || !TryGetImportExtension(request.Path, out _))
            {
                return Results.BadRequest(new { error = "path must use .json, .jsonl, .ndjson or .csv" });
            }

            if (!File.Exists(request.Path))
            {
                return Results.NotFound(new { error = "Source file was not found" });
            }

            var summary = data.Import(Guid.NewGuid().ToString("N"), request.Path, adapter, request.WorkspaceId, request.OwnerId);
            sources.MarkSuccess(request.Adapter, request.Path);
            return Results.Ok(summary);
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
        finally
        {
            jobs.ReleaseInline();
        }
    }

    private static bool TryGetImportExtension(string fileName, out string extension)
    {
        extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension is ".json" or ".jsonl" or ".ndjson" or ".csv";
    }

    private static IResult RemoveTag(HttpRequest request, string scope, string entityId, string tagReference, DashboardDataService data)
    {
        var table = scope.ToLowerInvariant() switch
        {
            "source" => "source_tags",
            "session" => "session_tags",
            "project" => "project_tags",
            _ => null
        };
        if (table is null)
        {
            return Results.BadRequest(new { error = "scope must be source, session or project" });
        }

        var entityColumn = table == "source_tags" ? "source_id" : table == "session_tags" ? "session_id" : "project_id";
        var value = request.Query["value"].ToString();
        var assignment = data.Query($"""
            SELECT t.tag_id, t.tag_key, t.tag_value
            FROM tags AS t
            INNER JOIN {table} AS assignment ON assignment.tag_id = t.tag_id
            WHERE assignment.{entityColumn} = $entityId AND t.tag_id = $reference;
            """, ("$entityId", entityId), ("$reference", tagReference)).ToArray();
        if (assignment.Length == 0)
        {
            assignment = data.Query($"""
                SELECT t.tag_id, t.tag_key, t.tag_value
                FROM tags AS t
                INNER JOIN {table} AS assignment ON assignment.tag_id = t.tag_id
                WHERE assignment.{entityColumn} = $entityId
                  AND t.tag_key = $reference
                  AND ($value = '' OR t.tag_value = $value);
                """, ("$entityId", entityId), ("$reference", tagReference), ("$value", value)).ToArray();
        }

        if (assignment.Length == 0)
        {
            return Results.NotFound(new { error = "The tag assignment was not found" });
        }

        if (assignment.Length > 1)
        {
            return Results.BadRequest(new { error = "The tag key is ambiguous; provide ?value=" });
        }

        var resolvedTagId = Convert.ToString(assignment[0]["tag_id"], System.Globalization.CultureInfo.InvariantCulture)!;
        data.Transaction((connection, transaction) =>
        {
            Execute(connection, transaction, $"DELETE FROM {table} WHERE {entityColumn} = $entityId AND tag_id = $tagId;", ("$entityId", entityId), ("$tagId", resolvedTagId));
            Execute(connection, transaction, "DELETE FROM tags WHERE tag_id = $tagId AND NOT EXISTS (SELECT 1 FROM source_tags WHERE tag_id = $tagId) AND NOT EXISTS (SELECT 1 FROM session_tags WHERE tag_id = $tagId) AND NOT EXISTS (SELECT 1 FROM project_tags WHERE tag_id = $tagId);", ("$tagId", resolvedTagId));
        });
        data.RebuildFts();
        return Results.NoContent();
    }

    private static IResult Export(ExportRequest request, DashboardReadService dashboard, DashboardDataService data, HttpResponse response)
    {
        var format = request.Format.Trim().ToLowerInvariant();
        var range = string.IsNullOrWhiteSpace(request.Preset) && string.IsNullOrWhiteSpace(request.From) && string.IsNullOrWhiteSpace(request.To)
            ? new DateRange(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, DateRangeResolver.FindTimeZone(request.TimeZone).Id)
            : DateRangeResolver.Resolve(request.Preset, request.From, request.To, request.TimeZone);
        var events = dashboard.Events(range);
        if (format == "csv")
        {
            var builder = new StringBuilder("date,event_count,input_tokens,cached_input_tokens,output_tokens,cache_hit_rate,cost_usd\n");
            foreach (var row in dashboard.Daily(range))
            {
                var json = JsonSerializer.Serialize(row);
                using var document = JsonDocument.Parse(json);
                var value = document.RootElement;
                builder.Append(value.GetProperty("date").GetString()).Append(',')
                    .Append(value.GetProperty("eventCount").GetInt32()).Append(',')
                    .Append(value.GetProperty("inputTokens").GetInt64()).Append(',')
                    .Append(value.GetProperty("cachedInputTokens").GetInt64()).Append(',')
                    .Append(value.GetProperty("outputTokens").GetInt64()).Append(',')
                    .Append(value.GetProperty("cacheHitRate").ToString()).Append(',')
                    .Append(value.GetProperty("costUsd").ToString()).Append('\n');
            }

            return Results.File(Encoding.UTF8.GetBytes(builder.ToString()), "text/csv", "token-dashboard.csv");
        }

        if (format == "json")
        {
            if (request.IncludeContent && !request.ConfirmIncludeContent)
            {
                return Results.BadRequest(new { error = "ConfirmIncludeContent is required for JSON exports containing conversation content" });
            }

            if (request.IncludeContent)
            {
                response.Headers["X-Token-Dashboard-Export-Warning"] = "Contains complete conversation content and may include sensitive data";
            }

            var payload = new
            {
                warning = request.IncludeContent ? "Contains complete conversation content and may include sensitive data" : null,
                range,
                events = events.Select(item => request.IncludeContent ? item : item with { Prompt = "", Response = "", Payload = "" })
            };
            if (request.IncludeContent)
            {
                return Results.File(JsonSerializer.SerializeToUtf8Bytes(payload), "application/json", "token-dashboard.json");
            }

            return Results.File(JsonSerializer.SerializeToUtf8Bytes(payload), "application/json", "token-dashboard.json");
        }

        if (format == "sqlite" || format == "db")
        {
            if (request.IncludeContent && !request.ConfirmIncludeContent)
            {
                return Results.BadRequest(new { error = "ConfirmIncludeContent is required for SQLite exports containing conversation content" });
            }

            response.Headers["X-Token-Dashboard-Export-Warning"] = request.IncludeContent
                ? "SQLite export contains complete conversation content and may include sensitive data"
                : "SQLite export is scrubbed by default; conversation content is excluded";

            return Results.File(data.Backup(request.IncludeContent), "application/x-sqlite3", "token-dashboard.sqlite");
        }

        return Results.BadRequest(new { error = "format must be csv, json or sqlite" });
    }

    private static IResult Delete(DeleteDataRequest request, DashboardDataService data)
    {
        if (!request.ClearAll && (request.SessionIds is null || request.SessionIds.Count == 0) && (request.SourceIds is null || request.SourceIds.Count == 0))
        {
            return Results.BadRequest(new { error = "clearAll or a selection is required" });
        }

        data.Transaction((connection, transaction) =>
        {
            if (request.ClearAll)
            {
                foreach (var table in ClearTables)
                {
                    Execute(connection, transaction, $"DELETE FROM {table};");
                }

                if (request.RemoveManagedSources)
                {
                    Execute(connection, transaction, "DELETE FROM managed_sources;");
                }

                return;
            }

            foreach (var id in request.SessionIds ?? [])
            {
                Execute(connection, transaction, "DELETE FROM sessions WHERE session_id = $id;", ("$id", id));
            }

            foreach (var id in request.SourceIds ?? [])
            {
                Execute(connection, transaction, "DELETE FROM sources WHERE source_id = $id;", ("$id", id));
            }
        });
        data.RebuildFts();
        return Results.NoContent();
    }

    private static void Execute(Microsoft.Data.Sqlite.SqliteConnection connection, Microsoft.Data.Sqlite.SqliteTransaction transaction, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

        command.ExecuteNonQuery();
    }
}

public partial class Program
{
}

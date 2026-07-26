using System.Net;
using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TokenDashboard.Api;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace TokenDashboard.Api.Tests;

public sealed class ApiIntegrationTests
{
    [Fact]
    public async Task HealthIsAnonymousButApiRequiresSessionKey()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/overview")).StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/overview");
        request.Headers.Add(SessionKeyMiddleware.HeaderName, factory.Key);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task CorsAllowsOnlyTheActualLoopbackOrigin()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        using var allowed = new HttpRequestMessage(HttpMethod.Options, "/api/overview");
        allowed.Headers.Add("Origin", "http://localhost");
        allowed.Headers.Add("Access-Control-Request-Method", "GET");
        allowed.Headers.Add("Access-Control-Request-Headers", "X-Token-Dashboard-Key");
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(allowed)).StatusCode);

        using var rejectedHeader = new HttpRequestMessage(HttpMethod.Options, "/api/overview");
        rejectedHeader.Headers.Add("Origin", "http://localhost");
        rejectedHeader.Headers.Add("Access-Control-Request-Method", "GET");
        rejectedHeader.Headers.Add("Access-Control-Request-Headers", "X-Evil-Header");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(rejectedHeader)).StatusCode);

        using var rejected = new HttpRequestMessage(HttpMethod.Options, "/api/overview");
        rejected.Headers.Add("Origin", "https://example.invalid");
        rejected.Headers.Add("Access-Control-Request-Method", "GET");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(rejected)).StatusCode);
    }

    [Fact]
    public async Task ActualKestrelHostBindsLoopbackWithDynamicPort()
    {
        await using var app = ProgramEntry.BuildApplication([
            "--TokenDashboard:ConnectionString=Data Source=:memory:;Mode=Memory;Cache=Shared",
            "--TokenDashboard:OpenBrowser=false",
            "--TokenDashboard:EmitStartupDiagnostics=false"
        ]);
        await app.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
            var address = Assert.Single(addresses);
            var uri = new Uri(address);
            Assert.Equal("127.0.0.1", uri.Host);
            Assert.NotEqual(0, uri.Port);

            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{uri.Port}") };
            var spa = await client.GetAsync("/");
            Assert.Equal(HttpStatusCode.OK, spa.StatusCode);
            Assert.Contains("<div id=\"app\"></div>", await spa.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/overview")).StatusCode);

            using var authenticated = new HttpRequestMessage(HttpMethod.Get, "/api/overview");
            authenticated.Headers.Add(SessionKeyMiddleware.HeaderName, app.Services.GetRequiredService<SessionKeyService>().Key);
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(authenticated)).StatusCode);
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task ExplicitContainerPortsPreserveLoopbackBinding()
    {
        var listenPort = AvailableLoopbackPort();
        await using var app = ProgramEntry.BuildApplication([
            "--TokenDashboard:ConnectionString=Data Source=:memory:;Mode=Memory;Cache=Shared",
            "--TokenDashboard:OpenBrowser=false",
            $"--TokenDashboard:ListenPort={listenPort}",
            "--TokenDashboard:BrowserPort=18080"
        ]);
        await app.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
            var address = new Uri(Assert.Single(addresses));
            Assert.Equal("127.0.0.1", address.Host);
            Assert.Equal(listenPort, address.Port);
            Assert.Equal(
                18080,
                app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ApiOptions>>().Value.BrowserPort);
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task DiscoveryAndCapabilitiesExposeFourAdapters()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateAuthenticatedClient();
        var capabilities = await client.GetFromJsonAsync<JsonElement>("/api/sources/capabilities");
        Assert.Equal(4, capabilities.GetArrayLength());
        Assert.Contains("projects", (await client.GetStringAsync("/api/sources/discovery?adapter=claude-code-app")));
        Assert.Contains("json", (await client.GetStringAsync("/api/sources/capabilities")));
    }

    [Fact]
    public async Task AutoDiscoveryReturnsAllFourAdaptersWithIndependentCapabilitiesAndPaths()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateAuthenticatedClient();
        var auto = await client.GetFromJsonAsync<JsonElement>("/api/sources/discovery?adapter=auto");
        Assert.Equal(4, auto.GetArrayLength());
        Assert.Equal(
            ["ClaudeCodeApp", "ClaudeCodeCli", "CodexApp", "CodexCli"],
            auto.EnumerateArray().Select(item => item.GetProperty("adapter").GetString()!).ToArray());
        Assert.All(auto.EnumerateArray(), item =>
        {
            Assert.Equal(JsonValueKind.Object, item.GetProperty("capabilities").ValueKind);
            Assert.Equal(JsonValueKind.Array, item.GetProperty("paths").ValueKind);
            Assert.Contains("json", item.GetProperty("capabilities").GetProperty("formats").EnumerateArray().Select(value => value.GetString()));
        });
    }

    [Fact]
    public async Task MainReadEndpointsAreAvailableWithDateFilters()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateAuthenticatedClient();
        var path = WriteFixture("""{"source_id":"endpoint-source","session_id":"endpoint-session","turn_id":"endpoint-turn","occurred_at_utc":"2026-07-08T00:00:00Z","source_timezone":"UTC","model":"gpt-5.4","input_tokens":1}""");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/sources/import", new SourceImportRequest("codex-cli", path))).StatusCode);
        foreach (var route in new[]
        {
            "/api/usage/daily?from=2026-07-08&to=2026-07-09",
            "/api/usage/monthly?from=2026-07-08&to=2026-07-09",
            "/api/comparisons?from=2026-07-08&to=2026-07-09",
            "/api/heatmap?from=2026-07-08&to=2026-07-09",
            "/api/sessions?from=2026-07-08&to=2026-07-09",
            "/api/sessions/endpoint-session"
        })
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(route)).StatusCode);
        }
    }

    [Fact]
    public async Task ImportSupportsTimezoneGroupingFtsTagsAndPricingCatalog()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateAuthenticatedClient();
        var path = WriteFixture("""
            [
              {"source_id":"codex-test","session_id":"session-test","turn_id":"turn-1","sequence":1,"event_type":"turn.completed","occurred_at_utc":"2026-07-01T15:59:00Z","source_timezone":"Asia/Taipei","role":"user","prompt":"before boundary","response":"answer one","model":"gpt-5.4","input_tokens":10,"cached_input_tokens":30,"output_tokens":4,"tool":"shell","subagent":"worker","workflow":"review","tags":["project:test"]},
              {"source_id":"codex-test","session_id":"session-test","turn_id":"turn-2","sequence":2,"event_type":"turn.completed","occurred_at_utc":"2026-07-01T16:00:00Z","source_timezone":"Asia/Taipei","role":"assistant","prompt":"after boundary","response":"answer two","model":"gpt-5.4","input_tokens":20,"output_tokens":8}
            ]
            """);
        var imported = await client.PostAsJsonAsync("/api/sources/import", new SourceImportRequest("codex-cli", path));
        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);

        var daily = await client.GetFromJsonAsync<JsonElement>("/api/usage/daily?from=2026-07-01&to=2026-07-03&timeZone=Asia/Taipei");
        Assert.Equal(2, daily.GetArrayLength());
        var overview = await client.GetFromJsonAsync<JsonElement>("/api/overview?from=2026-07-01&to=2026-07-03&timeZone=Asia/Taipei");
        Assert.Equal(0.5m, overview.GetProperty("cacheHitRate").GetDecimal());
        var searchText = await client.GetStringAsync("/api/search?q=before%20boundary");
        var search = JsonSerializer.Deserialize<JsonElement>(searchText);
        Assert.True(search.GetProperty("results").GetArrayLength() > 0);

        var tag = await client.PostAsJsonAsync("/api/tags", new TagRequest("session", "session-test", "kind", "important"));
        Assert.Equal(HttpStatusCode.OK, tag.StatusCode);
        Assert.Contains("important", await client.GetStringAsync("/api/tags"));
        Assert.Contains("gpt-5.4", await client.GetStringAsync("/api/pricing"));
    }

    [Fact]
    public async Task ExportSeparatesStatsFromSensitiveContentAndDeleteKeepsFtsConsistent()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateAuthenticatedClient();
        var path = WriteFixture("""{"source_id":"export-source","session_id":"export-session","turn_id":"export-turn","occurred_at_utc":"2026-07-02T00:00:00Z","source_timezone":"UTC","prompt":"private prompt","response":"private response","model":"unknown-model","input_tokens":1,"output_tokens":1}""");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/sources/import", new SourceImportRequest("codex-cli", path))).StatusCode);

        var csv = await client.PostAsJsonAsync("/api/export", new ExportRequest("csv"));
        var csvBody = await csv.Content.ReadAsStringAsync();
        Assert.DoesNotContain("private prompt", csvBody);

        var json = await client.PostAsJsonAsync("/api/export", new ExportRequest("json"));
        var jsonBody = await json.Content.ReadAsStringAsync();
        Assert.Contains("private prompt", jsonBody);
        Assert.Contains("sensitive", jsonBody);
        Assert.True(json.Headers.Contains("X-Token-Dashboard-Export-Warning"));

        var sqlite = await client.PostAsJsonAsync("/api/export", new ExportRequest("sqlite"));
        Assert.True(sqlite.Headers.Contains("X-Token-Dashboard-Export-Warning"));
        Assert.Equal("SQLite format 3", Encoding.UTF8.GetString((await sqlite.Content.ReadAsByteArrayAsync())[0..15]));

        using var delete = new HttpRequestMessage(HttpMethod.Delete, "/api/data") { Content = JsonContent.Create(new DeleteDataRequest(true)) };
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(delete)).StatusCode);
        var search = await client.GetFromJsonAsync<JsonElement>("/api/search?q=private");
        Assert.Equal(0, search.GetProperty("results").GetArrayLength());
    }

    [Fact]
    public async Task SyncReportsPartialSuccessAndUnknownModelsRemainUnpriced()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateAuthenticatedClient();
        var path = WriteFixture("""{"source_id":"sync-source","session_id":"sync-session","turn_id":"sync-turn","occurred_at_utc":"2026-07-03T00:00:00Z","source_timezone":"UTC","model":"not-in-catalog","input_tokens":3}""");
        var response = await client.PostAsJsonAsync("/api/sync", new SyncRequest("codex-cli", [path, Path.Combine(Path.GetTempPath(), "missing-token-dashboard.json")]));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var queued = await response.Content.ReadFromJsonAsync<JsonElement>();
        var syncId = queued.GetProperty("syncId").GetGuid();
        JsonElement status = default;
        for (var attempt = 0; attempt < 30; attempt++)
        {
            await Task.Delay(25);
            status = await client.GetFromJsonAsync<JsonElement>($"/api/sync/{syncId}");
            if (status.GetProperty("status").GetString() is "partial" or "failed" or "completed")
            {
                break;
            }
        }

        Assert.Equal("partial", status.GetProperty("status").GetString());
        var overview = await client.GetFromJsonAsync<JsonElement>("/api/overview?from=2026-07-03&to=2026-07-04");
        Assert.True(overview.GetProperty("unpriced").GetBoolean());
        Assert.Equal(1, overview.GetProperty("unpricedCount").GetInt32());
    }

    [Fact]
    public async Task StatisticsCountEachTurnOnceWhileSessionDetailKeepsAllSubEvents()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateAuthenticatedClient();
        var path = WriteFixture("""
            [
              {"source_id":"multi-event","session_id":"multi-session","turn_id":"multi-turn","sequence":1,"event_type":"turn.started","occurred_at_utc":"2026-07-04T00:00:00Z","source_timezone":"UTC","prompt":"one turn","model":"gpt-5.4","input_tokens":10,"output_tokens":5},
              {"source_id":"multi-event","session_id":"multi-session","turn_id":"multi-turn","sequence":2,"event_type":"tool.completed","occurred_at_utc":"2026-07-04T00:00:01Z","source_timezone":"UTC","tool":"shell","model":"gpt-5.4"}
            ]
            """);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/sources/import", new SourceImportRequest("codex-cli", path))).StatusCode);

        var overview = await client.GetFromJsonAsync<JsonElement>("/api/overview?from=2026-07-04&to=2026-07-05");
        Assert.Equal(1, overview.GetProperty("eventCount").GetInt32());
        Assert.Equal(10, overview.GetProperty("inputTokens").GetInt64());
        Assert.Equal(5, overview.GetProperty("outputTokens").GetInt64());
        Assert.Equal(0, overview.GetProperty("unpricedCount").GetInt32());

        var detail = await client.GetFromJsonAsync<JsonElement>("/api/sessions/multi-session");
        Assert.Equal(2, detail.GetProperty("turns")[0].GetProperty("subEvents").GetArrayLength());
        Assert.Equal(2, detail.GetProperty("turns")[0].GetProperty("tokenUsage").GetArrayLength());
        Assert.Equal(15, detail.GetProperty("totalTokens").GetInt64());
        Assert.Equal(10, detail.GetProperty("tokens").GetProperty("input").GetInt64());
        Assert.Equal(5, detail.GetProperty("tokens").GetProperty("output").GetInt64());
        Assert.Equal(10, detail.GetProperty("turns")[0].GetProperty("tokens").GetProperty("input").GetInt64());
        Assert.Equal(5, detail.GetProperty("turns")[0].GetProperty("tokens").GetProperty("output").GetInt64());
        Assert.Equal(overview.GetProperty("costUsd").GetDecimal(), detail.GetProperty("costUsd").GetDecimal());
        Assert.Equal(overview.GetProperty("costUsd").GetDecimal(), detail.GetProperty("turns")[0].GetProperty("costUsd").GetDecimal());
    }

    [Fact]
    public async Task UnpricedCountUsesUniqueTurnEvenWhenTurnHasMultipleSubEvents()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateAuthenticatedClient();
        var path = WriteFixture("""
            [
              {"source_id":"unpriced-multi","session_id":"unpriced-session","turn_id":"unpriced-turn","sequence":1,"occurred_at_utc":"2026-07-16T00:00:00Z","model":"unknown-unpriced","input_tokens":2},
              {"source_id":"unpriced-multi","session_id":"unpriced-session","turn_id":"unpriced-turn","sequence":2,"occurred_at_utc":"2026-07-16T00:00:01Z","model":"unknown-unpriced","tool":"shell"}
            ]
            """);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/sources/import", new SourceImportRequest("codex-cli", path))).StatusCode);
        var overview = await client.GetFromJsonAsync<JsonElement>("/api/overview?from=2026-07-16&to=2026-07-17");
        Assert.True(overview.GetProperty("unpriced").GetBoolean());
        Assert.Equal(1, overview.GetProperty("unpricedCount").GetInt32());
    }

    [Fact]
    public async Task PricingUsesUserOverrideHistoryAndAutomaticLongContextThreshold()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateAuthenticatedClient();
        var normalPath = WriteFixture("""{"source_id":"price-source","session_id":"price-session","turn_id":"price-turn","occurred_at_utc":"2026-04-01T00:00:00Z","source_timezone":"UTC","model":"gpt-5.4","input_tokens":100}""");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/sources/import", new SourceImportRequest("codex-cli", normalPath))).StatusCode);
        var before = await client.GetFromJsonAsync<JsonElement>("/api/overview?from=2026-04-01&to=2026-04-02");

        var pricing = await client.PutAsJsonAsync("/api/pricing", new PriceWriteRequest(
            "openai", "gpt-5.4", "input", 100m,
            MaximumInputTokens: 272000,
            EffectiveFromUtc: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            SourceName: "test override", SourceUrl: "https://example.invalid/override"));
        Assert.Equal(HttpStatusCode.OK, pricing.StatusCode);
        var after = await client.GetFromJsonAsync<JsonElement>("/api/overview?from=2026-04-01&to=2026-04-02");
        Assert.True(after.GetProperty("costUsd").GetDecimal() > before.GetProperty("costUsd").GetDecimal());

        var longPath = WriteFixture("""{"source_id":"long-source","session_id":"long-session","turn_id":"long-turn","occurred_at_utc":"2026-04-02T00:00:00Z","source_timezone":"UTC","model":"gpt-5.4","input_tokens":272001}""");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/sources/import", new SourceImportRequest("codex-cli", longPath))).StatusCode);
        var longOverview = await client.GetFromJsonAsync<JsonElement>("/api/overview?from=2026-04-02&to=2026-04-03");
        Assert.True(longOverview.GetProperty("costUsd").GetDecimal() > after.GetProperty("costUsd").GetDecimal());

        var fastPricing = new PriceWriteRequest(
            "openai", "gpt-5.4", "input", 200m, "fast",
            EffectiveFromUtc: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var fastPricingResponse = await client.PutAsJsonAsync("/api/pricing", fastPricing);
        Assert.Equal(HttpStatusCode.OK, fastPricingResponse.StatusCode);
        var fastPath = WriteFixture("""{"source_id":"fast-source","session_id":"fast-session","turn_id":"fast-turn","occurred_at_utc":"2026-04-03T00:00:00Z","source_timezone":"UTC","model":"gpt-5.4","mode":"fast","input_tokens":100}""");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/sources/import", new SourceImportRequest("codex-cli", fastPath))).StatusCode);
        var fastOverview = await client.GetFromJsonAsync<JsonElement>("/api/overview?from=2026-04-03&to=2026-04-04");
        Assert.Equal(0.02m, fastOverview.GetProperty("costUsd").GetDecimal());
    }

    [Fact]
    public async Task CacheReadAliasesUseCacheReadOverCacheableInput()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateAuthenticatedClient();
        var content = "{\"source_id\":\"cache-source\",\"session_id\":\"cache-session\",\"turn_id\":\"cache-turn\",\"occurred_at_utc\":\"2026-07-05T00:00:00Z\",\"source_timezone\":\"UTC\",\"model\":\"gpt-5.4\",\"input_tokens\":75,\"cache_read_tokens\":25}";
        var response = await client.PostAsJsonAsync("/api/sources/import", new SourceImportRequest("codex-cli", null, "cache.json", content));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var overview = await client.GetFromJsonAsync<JsonElement>("/api/overview?from=2026-07-05&to=2026-07-06");
        Assert.Equal(0.25m, overview.GetProperty("cacheHitRate").GetDecimal());
        Assert.Equal(25, overview.GetProperty("cachedInputTokens").GetInt64());
    }

    [Fact]
    public async Task ContentImportDeletesTemporaryFileAndValidatesExtension()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateAuthenticatedClient();
        var content = "{\"source_id\":\"inline-source\",\"session_id\":\"inline-session\",\"turn_id\":\"inline-turn\",\"occurred_at_utc\":\"2026-07-06T00:00:00Z\",\"source_timezone\":\"UTC\",\"prompt\":\"inline prompt\"}";
        var response = await client.PostAsJsonAsync("/api/sources/import", new SourceImportRequest("codex-cli", null, "inline.json", content));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(Directory.GetFiles(Path.GetTempPath(), "token-dashboard-import-*.json"));

        var invalid = await client.PostAsJsonAsync("/api/sources/import", new SourceImportRequest("codex-cli", null, "inline.exe", content));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task ProjectTagAssignmentIsSeparateAndTagDeleteRebuildsFts()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateAuthenticatedClient();
        var path = WriteFixture("""{"source_id":"tag-source","session_id":"tag-session","turn_id":"tag-turn","occurred_at_utc":"2026-07-07T00:00:00Z","source_timezone":"UTC","prompt":"tag prompt"}""");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/sources/import", new SourceImportRequest("codex-cli", path))).StatusCode);

        var projectTag = await client.PostAsJsonAsync("/api/tags", new TagRequest("project", "project-1", "project", "alpha"));
        Assert.Equal(HttpStatusCode.OK, projectTag.StatusCode);
        Assert.Contains("alpha", await client.GetStringAsync("/api/tags"));
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/tags/project/project-1/project")).StatusCode);
        Assert.DoesNotContain("alpha", await client.GetStringAsync("/api/tags"));

        var secondPath = WriteFixture("""{"source_id":"tag-source","session_id":"tag-session-2","turn_id":"tag-turn-2","occurred_at_utc":"2026-07-07T00:00:01Z","source_timezone":"UTC","prompt":"second tag session"}""");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/sources/import", new SourceImportRequest("codex-cli", secondPath))).StatusCode);
        var sessionTag = await client.PostAsJsonAsync("/api/tags", new TagRequest("session", "tag-session", "searchable", "needle"));
        Assert.Equal(HttpStatusCode.OK, sessionTag.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/tags", new TagRequest("session", "tag-session-2", "searchable", "needle"))).StatusCode);
        Assert.True((await client.GetFromJsonAsync<JsonElement>("/api/search?q=needle")).GetProperty("results").GetArrayLength() > 0);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/tags/session/tag-session/searchable")).StatusCode);
        Assert.Contains("needle", await client.GetStringAsync("/api/tags"));
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/tags/session/tag-session-2/searchable")).StatusCode);
        Assert.Equal(0, (await client.GetFromJsonAsync<JsonElement>("/api/search?q=needle")).GetProperty("results").GetArrayLength());
    }

    [Fact]
    public async Task SyncWithoutPathsDiscoversEachAdapterAndExpandsSupportedFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"token-dashboard-discovery-{Guid.NewGuid():N}");
        var appData = Path.Combine(root, "appdata");
        Directory.CreateDirectory(Path.Combine(root, ".claude", "projects", "nested"));
        Directory.CreateDirectory(Path.Combine(root, ".codex", "sessions"));
        Directory.CreateDirectory(Path.Combine(root, ".codex", "archived_sessions"));
        Directory.CreateDirectory(Path.Combine(appData, "Claude", "logs"));
        try
        {
            WriteFile(Path.Combine(root, ".claude", "projects", "nested", "claude-cli.json"), EventJson("claude-cli-source", "claude-cli-session", "claude-cli-turn"));
            WriteFile(Path.Combine(root, ".codex", "sessions", "codex-app.jsonl"), EventJson("codex-app-source", "codex-app-session", "codex-app-turn"));
            WriteFile(Path.Combine(root, ".codex", "archived_sessions", "codex-cli.csv"), "source_id,session_id,turn_id,occurred_at_utc,input_tokens\ncodex-cli-source,codex-cli-session,codex-cli-turn,2026-07-10T00:00:00Z,1\n");
            WriteFile(Path.Combine(appData, "Claude", "logs", "claude-app.json"), EventJson("claude-app-source", "claude-app-session", "claude-app-turn"));
            WriteFile(Path.Combine(root, ".codex", "sessions", "skip.txt"), "not supported");

            using var factory = new ApiFactory { SourceHome = root, SourceAppData = appData };
            using var client = factory.CreateAuthenticatedClient();
            var response = await client.PostAsJsonAsync("/api/sync", new SyncRequest(null, null));
            var queued = await response.Content.ReadFromJsonAsync<JsonElement>();
            var status = await WaitForSync(client, queued.GetProperty("syncId").GetGuid());

            Assert.Equal("completed", status.GetProperty("status").GetString());
            Assert.Equal(4, status.GetProperty("imports").GetArrayLength());
            Assert.Equal(4, status.GetProperty("imports").EnumerateArray().Sum(item => item.GetProperty("importedEventCount").GetInt32()));
            var sources = factory.Services.GetRequiredService<DashboardDataService>().Query("SELECT source_id, adapter_kind FROM sources ORDER BY source_id;");
            Assert.Equal("ClaudeCodeApp", sources.Single(row => Convert.ToString(row["source_id"], CultureInfo.InvariantCulture) == "claude-app-source")["adapter_kind"]);
            Assert.Equal("ClaudeCodeCli", sources.Single(row => Convert.ToString(row["source_id"], CultureInfo.InvariantCulture) == "claude-cli-source")["adapter_kind"]);
            Assert.Equal("CodexApp", sources.Single(row => Convert.ToString(row["source_id"], CultureInfo.InvariantCulture) == "codex-app-source")["adapter_kind"]);
            Assert.Equal("CodexCli", sources.Single(row => Convert.ToString(row["source_id"], CultureInfo.InvariantCulture) == "codex-cli-source")["adapter_kind"]);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public async Task SyncCustomDirectoryExpandsJsonJsonlCsvAndPreservesPartialSuccess()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"token-dashboard-custom-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(directory, "nested"));
        try
        {
            WriteFile(Path.Combine(directory, "one.json"), EventJson("custom-one", "custom-session-one", "custom-turn-one"));
            WriteFile(Path.Combine(directory, "nested", "two.csv"), "source_id,session_id,turn_id,occurred_at_utc,input_tokens\ncustom-two,custom-session-two,custom-turn-two,2026-07-11T00:00:00Z,1\n");
            WriteFile(Path.Combine(directory, "skip.log"), "unsupported");
            using var factory = new ApiFactory();
            using var client = factory.CreateAuthenticatedClient();
            var response = await client.PostAsJsonAsync("/api/sync", new SyncRequest("codex-cli", [directory]));
            var queued = await response.Content.ReadFromJsonAsync<JsonElement>();
            var status = await WaitForSync(client, queued.GetProperty("syncId").GetGuid());
            Assert.Equal("completed", status.GetProperty("status").GetString());
            Assert.Equal(2, status.GetProperty("imports").EnumerateArray().Sum(item => item.GetProperty("importedEventCount").GetInt32()));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Fact]
    public async Task RawOnlyFieldsAreNotPersistedOrExported()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateAuthenticatedClient();
        const string sentinel = "raw-only-sentinel-7f2a";
        var content = "{\"source_id\":\"raw-source\",\"session_id\":\"raw-session\",\"turn_id\":\"raw-turn\",\"occurred_at_utc\":\"2026-07-12T00:00:00Z\",\"source_timezone\":\"UTC\",\"prompt\":\"kept prompt\",\"" + sentinel + "\":\"must not persist\",\"input_tokens\":1}";
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/sources/import", new SourceImportRequest("codex-cli", null, "raw.json", content))).StatusCode);
        var storePayload = factory.Services.GetRequiredService<DashboardDataService>().Query("SELECT payload FROM sub_events WHERE source_id = 'raw-source';").Single()["payload"]?.ToString();
        Assert.DoesNotContain(sentinel, storePayload);
        var json = await client.PostAsJsonAsync("/api/export", new ExportRequest("json"));
        Assert.DoesNotContain(sentinel, await json.Content.ReadAsStringAsync());
        var sqlite = await client.PostAsJsonAsync("/api/export", new ExportRequest("sqlite"));
        Assert.DoesNotContain(sentinel, Encoding.UTF8.GetString(await sqlite.Content.ReadAsByteArrayAsync()));
    }

    [Fact]
    public async Task DynamicTokenTypesFiltersAndSessionCostRemainVisible()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateAuthenticatedClient();
        var path = WriteFixture("""{"source_id":"dynamic-source","session_id":"dynamic-session","turn_id":"dynamic-turn","occurred_at_utc":"2026-07-13T00:00:00Z","source_timezone":"UTC","model":"gpt-5.4","tool":"shell","input_tokens":2,"reasoning_tokens":7,"fast_tokens":3,"output_tokens":1}""");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/sources/import", new SourceImportRequest("codex-cli", path))).StatusCode);
        var overview = await client.GetFromJsonAsync<JsonElement>("/api/overview?from=2026-07-13&to=2026-07-14");
        Assert.Equal(13, overview.GetProperty("totalTokens").GetInt64());
        Assert.Equal(7, overview.GetProperty("tokenTypes").GetProperty("reasoning").GetInt64());
        Assert.Equal(3, overview.GetProperty("tokens").GetProperty("fast").GetInt64());
        var reasoning = await client.GetFromJsonAsync<JsonElement>("/api/usage/daily?from=2026-07-13&to=2026-07-14&tokenType=reasoning");
        Assert.Equal(7, reasoning[0].GetProperty("totalTokens").GetInt64());
        var comparison = await client.GetFromJsonAsync<JsonElement>("/api/comparisons?from=2026-07-13&to=2026-07-14&groupBy=tool");
        Assert.Equal("shell", comparison[0].GetProperty("key").GetString());
        Assert.Equal(13, comparison[0].GetProperty("totalTokens").GetInt64());
        var detail = await client.GetFromJsonAsync<JsonElement>("/api/sessions/dynamic-session");
        Assert.Equal(7, detail.GetProperty("tokenTypes").GetProperty("reasoning").GetInt64());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("costUsd").ValueKind);
    }

    [Fact]
    public async Task DateStatisticsApplySourceToolModelAndTokenFiltersConsistently()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateAuthenticatedClient();
        var path = WriteFixture("""
            [
              {"source_id":"filter-source","session_id":"filter-a","turn_id":"filter-a-turn","occurred_at_utc":"2026-07-14T00:00:00Z","model":"model-a","tool":"shell","input_tokens":4},
              {"source_id":"other-source","session_id":"filter-b","turn_id":"filter-b-turn","occurred_at_utc":"2026-07-14T00:00:00Z","model":"model-b","tool":"editor","reasoning_tokens":9}
            ]
            """);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/sources/import", new SourceImportRequest("codex-cli", path))).StatusCode);
        foreach (var endpoint in new[] { "/api/overview", "/api/usage/daily", "/api/usage/monthly", "/api/comparisons", "/api/heatmap", "/api/sessions" })
        {
            var separator = endpoint.Contains('?') ? "&" : "?";
            var json = await client.GetFromJsonAsync<JsonElement>($"{endpoint}{separator}from=2026-07-14&to=2026-07-15&sourceId=filter-source&tool=shell&model=model-a&tokenType=input");
            if (json.ValueKind == JsonValueKind.Array)
            {
                Assert.NotEmpty(json.EnumerateArray());
                Assert.All(json.EnumerateArray(), item => Assert.Equal(4, item.GetProperty("totalTokens").GetInt64()));
            }
            else
            {
                Assert.Equal(4, json.GetProperty("totalTokens").GetInt64());
            }
        }
    }

    [Fact]
    public async Task TagsExposeAndHydrateIndependentPersistedAssignments()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateAuthenticatedClient();
        var path = WriteFixture("""{"source_id":"scope-source","session_id":"scope-session","turn_id":"scope-turn","occurred_at_utc":"2026-07-15T00:00:00Z","workspace_id":"workspace-scope","prompt":"scope"}""");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/sources/import", new SourceImportRequest("codex-cli", path, WorkspaceId: "workspace-scope"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/tags", new TagRequest("source", "scope-source", "scope", "source-value"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/tags", new TagRequest("session", "scope-session", "scope", "session-value"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/tags", new TagRequest("project", "workspace-scope", "scope", "project-value"))).StatusCode);
        var tags = await client.GetFromJsonAsync<JsonElement>("/api/tags");
        Assert.Equal(3, tags.GetArrayLength());
        Assert.Contains(tags.EnumerateArray(), tag => tag.GetProperty("scope").GetString() == "source" && tag.GetProperty("entityId").GetString() == "scope-source");
        var detail = await client.GetFromJsonAsync<JsonElement>("/api/sessions/scope-session");
        Assert.Equal("session", detail.GetProperty("tags")[0].GetProperty("scope").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/tags/project/workspace-scope/scope")).StatusCode);
        Assert.Equal(2, (await client.GetFromJsonAsync<JsonElement>("/api/tags")).GetArrayLength());
    }

    [Fact]
    public async Task PricingValidationAndDtoDistinguishBuiltInAndOverrides()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateAuthenticatedClient();
        var invalid = await client.PutAsJsonAsync("/api/pricing", new PriceWriteRequest("openai", "gpt-5.4", "input", 1m, MinimumInputTokens: 10, MaximumInputTokens: 10));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var from = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var equalDates = await client.PutAsJsonAsync("/api/pricing", new PriceWriteRequest("openai", "gpt-5.4", "input", 1m, EffectiveFromUtc: from, EffectiveToUtc: from));
        Assert.Equal(HttpStatusCode.BadRequest, equalDates.StatusCode);
        var beforeDates = await client.PutAsJsonAsync("/api/pricing", new PriceWriteRequest("openai", "gpt-5.4", "input", 1m, EffectiveFromUtc: from, EffectiveToUtc: from.AddTicks(-1)));
        Assert.Equal(HttpStatusCode.BadRequest, beforeDates.StatusCode);
        var valid = await client.PutAsJsonAsync("/api/pricing", new PriceWriteRequest("provider-test", "model-test", "standard", 4m, MinimumInputTokens: 2, MaximumInputTokens: 5, EffectiveFromUtc: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), SourceName: "Verifier", SourceUrl: "https://example.invalid/price"));
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        var pricing = await client.GetFromJsonAsync<JsonElement>("/api/pricing");
        Assert.Equal(1, pricing.GetProperty("overrideCount").GetInt32());
        var overrideEntry = pricing.GetProperty("entries").EnumerateArray().Single(entry => entry.GetProperty("provider").GetString() == "provider-test");
        Assert.True(overrideEntry.GetProperty("isOverride").GetBoolean());
        Assert.Equal(4m, overrideEntry.GetProperty("usdPerMillionTokens").GetDecimal());
        Assert.Equal(2, overrideEntry.GetProperty("minimumInputTokens").GetInt64());
        Assert.Equal(5, overrideEntry.GetProperty("maximumInputTokens").GetInt64());
        Assert.Equal("Verifier", overrideEntry.GetProperty("sourceName").GetString());
        Assert.Equal("https://example.invalid/price", overrideEntry.GetProperty("sourceUrl").GetString());
        Assert.True(overrideEntry.TryGetProperty("effectiveFrom", out _));
        Assert.True(overrideEntry.TryGetProperty("effectiveTo", out _));
    }

    private static string WriteFixture(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"token-dashboard-api-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, content);
        return path;
    }

    private static int AvailableLoopbackPort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string EventJson(string sourceId, string sessionId, string turnId) => $"{{\"source_id\":\"{sourceId}\",\"session_id\":\"{sessionId}\",\"turn_id\":\"{turnId}\",\"occurred_at_utc\":\"2026-07-10T00:00:00Z\",\"model\":\"gpt-5.4\",\"input_tokens\":1}}";

    private static async Task<JsonElement> WaitForSync(HttpClient client, Guid syncId)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            await Task.Delay(20);
            var status = await client.GetFromJsonAsync<JsonElement>($"/api/sync/{syncId}");
            if (status.GetProperty("status").GetString() is "partial" or "failed" or "completed")
            {
                return status;
            }
        }

        throw new TimeoutException("Sync did not complete deterministically");
    }
}

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public string Key => Services.GetRequiredService<SessionKeyService>().Key;

    public string? SourceHome { get; set; }

    public string? SourceAppData { get; set; }

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TokenDashboard:ConnectionString"] = $"Data Source=api-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared",
            ["TokenDashboard:OpenBrowser"] = "false",
            ["TokenDashboard:SourceHome"] = SourceHome,
            ["TokenDashboard:SourceAppData"] = SourceAppData
        }));
    }

    protected override void Dispose(bool disposing)
    {
        foreach (var file in Directory.GetFiles(Path.GetTempPath(), "token-dashboard-api-*.json"))
        {
            File.Delete(file);
        }

        base.Dispose(disposing);
    }

    public HttpClient CreateAuthenticatedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(SessionKeyMiddleware.HeaderName, Key);
        return client;
    }
}

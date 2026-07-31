using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using TokenDashboard.Core;
using TokenDashboard.Data;

namespace TokenDashboard.Api;

public sealed class ApiOptions
{
    public string ConnectionString { get; set; } = "Data Source=token-dashboard.db";

    public bool OpenBrowser { get; set; } = true;

    public string? BrowserHost { get; set; }

    public int? BrowserPort { get; set; }

    public bool EmitStartupDiagnostics { get; set; }

    public bool StartupEntryRedirect { get; set; }

    public int ListenPort { get; set; }

    public string? SourceHome { get; set; }

    public string? SourceAppData { get; set; }
}

public sealed class SessionKeyService
{
    private readonly string key = CreateKey();

    public string Key => key;

    public bool Matches(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        var left = Encoding.UTF8.GetBytes(key);
        var right = Encoding.UTF8.GetBytes(candidate);
        return CryptographicOperations.FixedTimeEquals(left, right);
    }

    private static string CreateKey()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }
}

public sealed class SessionKeyMiddleware
{
    public const string HeaderName = "X-Token-Dashboard-Key";

    private readonly RequestDelegate next;

    public SessionKeyMiddleware(RequestDelegate next) => this.next = next;

    public async Task InvokeAsync(HttpContext context, SessionKeyService sessionKey)
    {
        if (HttpMethods.IsOptions(context.Request.Method) ||
            context.Request.Path.Equals("/health", StringComparison.OrdinalIgnoreCase) ||
            !context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue(HeaderName, out var supplied) ||
            !sessionKey.Matches(supplied.ToString()))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "A valid local session key is required" });
            return;
        }

        await next(context);
    }
}

public sealed class StartupEntryRedirectMiddleware
{
    private readonly RequestDelegate next;

    public StartupEntryRedirectMiddleware(RequestDelegate next) => this.next = next;

    public async Task InvokeAsync(HttpContext context, SessionKeyService sessionKey, IOptions<ApiOptions> options)
    {
        if (options.Value.StartupEntryRedirect &&
            HttpMethods.IsGet(context.Request.Method) &&
            context.Request.Path == "/")
        {
            var location = $"{context.Request.PathBase}/index.html#key={Uri.EscapeDataString(sessionKey.Key)}";
            context.Response.Redirect(location, permanent: false);
            return;
        }

        await next(context);
    }
}

public sealed class LoopbackCorsMiddleware
{
    private static readonly HashSet<string> AllowedMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "GET", "POST", "PUT", "DELETE", "OPTIONS"
    };

    private readonly RequestDelegate next;

    public LoopbackCorsMiddleware(RequestDelegate next) => this.next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Headers.TryGetValue("Origin", out var originValues))
        {
            await next(context);
            return;
        }

            var origin = originValues.ToString();
        if (!IsAllowed(origin, context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        context.Response.Headers.AccessControlAllowOrigin = origin;
        context.Response.Headers.Vary = "Origin";
        if (HttpMethods.IsOptions(context.Request.Method))
        {
            var requestedMethod = context.Request.Headers.AccessControlRequestMethod.ToString();
            if (!AllowedMethods.Contains(requestedMethod))
            {
                context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                return;
            }

            var requestedHeaders = context.Request.Headers.AccessControlRequestHeaders.ToString()
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (requestedHeaders.Any(header => !string.Equals(header, SessionKeyMiddleware.HeaderName, StringComparison.OrdinalIgnoreCase) && !string.Equals(header, "Content-Type", StringComparison.OrdinalIgnoreCase)))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            context.Response.Headers.AccessControlAllowMethods = requestedMethod;
            context.Response.Headers.AccessControlAllowHeaders = SessionKeyMiddleware.HeaderName + ", Content-Type";
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        await next(context);
    }

    public static bool IsAllowed(string origin, HttpRequest request)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var parsed) ||
            parsed.Scheme is not ("http" or "https") ||
            parsed.Host is not ("localhost" or "127.0.0.1" or "::1"))
        {
            return false;
        }

        var requestHost = request.Host.Host;
        var requestPort = request.Host.Port ?? (string.Equals(request.Scheme, "https", StringComparison.OrdinalIgnoreCase) ? 443 : 80);
        var originPort = parsed.IsDefaultPort ? (parsed.Scheme == "https" ? 443 : 80) : parsed.Port;
        return string.Equals(parsed.Scheme, request.Scheme, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(parsed.Host, requestHost, StringComparison.OrdinalIgnoreCase) &&
               originPort == requestPort;
    }
}

public interface IBrowserLauncher
{
    void Open(string url);
}

public sealed class ProcessBrowserLauncher : IBrowserLauncher
{
    public void Open(string url)
    {
        _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
    }
}

public sealed class BrowserStartupService : IHostedService
{
    private readonly IHostApplicationLifetime lifetime;
    private readonly IServer server;
    private readonly SessionKeyService key;
    private readonly IBrowserLauncher launcher;
    private readonly IOptions<ApiOptions> options;
    private readonly IHostEnvironment environment;

    public BrowserStartupService(
        IHostApplicationLifetime lifetime,
        IServer server,
        SessionKeyService key,
        IBrowserLauncher launcher,
        IOptions<ApiOptions> options,
        IHostEnvironment environment)
    {
        this.lifetime = lifetime;
        this.server = server;
        this.key = key;
        this.launcher = launcher;
        this.options = options;
        this.environment = environment;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lifetime.ApplicationStarted.Register(OpenBrowser);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void OpenBrowser()
    {
        var emitDiagnostics = options.Value.EmitStartupDiagnostics && environment.IsDevelopment();
        if (!options.Value.OpenBrowser && !emitDiagnostics)
        {
            return;
        }

        var address = server.Features.Get<IServerAddressesFeature>()?.Addresses
            .Select(static value => Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null)
            .FirstOrDefault(static uri => uri is not null && (uri.Host is "localhost" or "127.0.0.1" or "[::1]"));
        if (address is null)
        {
            return;
        }

        var host = options.Value.BrowserHost ?? (address.Host == "[::1]" ? "127.0.0.1" : address.Host);
        var port = options.Value.BrowserPort ?? address.Port;
        if (port is <= 0 or > 65535)
        {
            throw new InvalidOperationException("Browser port must be between 1 and 65535");
        }

        var url = $"{address.Scheme}://{host}:{port}/#key={Uri.EscapeDataString(key.Key)}";
        if (emitDiagnostics)
        {
            Console.WriteLine($"TOKEN_DASHBOARD_STARTUP_URL={url}");
        }

        if (options.Value.OpenBrowser)
        {
            launcher.Open(url);
        }
    }
}

public sealed class DashboardStore : IDisposable
{
    private readonly SqliteDataStore store;
    private readonly object gate = new();

    public DashboardStore(IOptions<ApiOptions> options)
    {
        store = new SqliteDataStore(options.Value.ConnectionString);
    }

    public T Read<T>(Func<SqliteConnection, T> action)
    {
        lock (gate)
        {
            return action(store.Connection);
        }
    }

    public void Write(Action<SqliteConnection> action)
    {
        lock (gate)
        {
            action(store.Connection);
        }
    }

    public void Dispose() => store.Dispose();
}

public sealed class SourceAdapterRegistry
{
    private readonly IReadOnlyDictionary<SourceAdapterKind, ILogSourceAdapter> adapters = new Dictionary<SourceAdapterKind, ILogSourceAdapter>
    {
        [SourceAdapterKind.ClaudeCodeApp] = new ClaudeCodeAppAdapter(),
        [SourceAdapterKind.ClaudeCodeCli] = new ClaudeCodeCliAdapter(),
        [SourceAdapterKind.CodexApp] = new CodexAppAdapter(),
        [SourceAdapterKind.CodexCli] = new CodexCliAdapter()
    };

    public IEnumerable<ILogSourceAdapter> All => adapters.Values;

    public ILogSourceAdapter Get(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A source adapter is required for an explicit import", nameof(value));
        }

        var normalized = value.Replace("-", string.Empty, StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal);
        var match = adapters.Values.FirstOrDefault(item => item.Kind.ToString().Replace("-", string.Empty, StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal).Equals(normalized, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            return match;
        }

        if (Enum.TryParse<SourceAdapterKind>(value, true, out var kind) && adapters.TryGetValue(kind, out var parsed))
        {
            return parsed;
        }

        throw new ArgumentException("Unknown source adapter", nameof(value));
    }

    public ILogSourceAdapter? IdentifyAutoPath(string path)
    {
        var normalized = path.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
        var kind = normalized switch
        {
            var value when value.Contains("application support/claude", StringComparison.Ordinal) || value.Contains("appdata/claude", StringComparison.Ordinal) || value.Contains("/.config/claude", StringComparison.Ordinal) => SourceAdapterKind.ClaudeCodeApp,
            var value when value.Contains("/.claude/", StringComparison.Ordinal) => SourceAdapterKind.ClaudeCodeCli,
            var value when value.EndsWith("/.codex/archived_sessions", StringComparison.Ordinal) => SourceAdapterKind.CodexCli,
            var value when value.Contains("/.codex/sessions", StringComparison.Ordinal) => SourceAdapterKind.CodexApp,
            var value when value.Contains("claude", StringComparison.Ordinal) && value.Contains("app", StringComparison.Ordinal) => SourceAdapterKind.ClaudeCodeApp,
            var value when value.Contains("claude", StringComparison.Ordinal) => SourceAdapterKind.ClaudeCodeCli,
            var value when value.Contains("codex", StringComparison.Ordinal) && value.Contains("cli", StringComparison.Ordinal) => SourceAdapterKind.CodexCli,
            var value when value.Contains("codex", StringComparison.Ordinal) => SourceAdapterKind.CodexApp,
            _ => (SourceAdapterKind?)null
        };
        return kind is { } selected && adapters.TryGetValue(selected, out var adapter) ? adapter : null;
    }

    public IReadOnlyList<(ILogSourceAdapter Adapter, string Path)> DiscoverAutoSources(SourceDiscoveryOptions options)
    {
        var selected = new List<(ILogSourceAdapter Adapter, string Path)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var adapter in All)
        {
            foreach (var candidate in adapter.DiscoverPaths(options).Where(static item => item.Exists))
            {
                var identified = IdentifyAutoPath(candidate.Path);
                if (identified is not null && identified.Kind != adapter.Kind)
                {
                    continue;
                }

                if (seen.Add(Path.GetFullPath(candidate.Path)))
                {
                    selected.Add((identified ?? adapter, candidate.Path));
                }
            }
        }

        return selected;
    }

    public static HostPlatform CurrentPlatform => OperatingSystem.IsWindows() ? HostPlatform.Windows : OperatingSystem.IsMacOS() ? HostPlatform.MacOS : HostPlatform.Linux;

    public static string UserHome => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}

public sealed record SyncRequest(
    string? Adapter,
    IReadOnlyList<string>? Paths,
    string? WorkspaceId = null,
    string? OwnerId = null);

public sealed record SyncStatus(
    Guid SyncId,
    string Status,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    IReadOnlyList<ImportSummary> Imports,
    string? Error);

public sealed class SyncJobService
{
    private readonly Channel<(Guid Id, SyncRequest Request)> jobs = Channel.CreateUnbounded<(Guid, SyncRequest)>();
    private readonly ConcurrentDictionary<Guid, SyncStatus> statuses = new();

    public Guid Enqueue(SyncRequest request)
    {
        var id = Guid.NewGuid();
        statuses[id] = new SyncStatus(id, "queued", DateTimeOffset.UtcNow, null, [], null);
        jobs.Writer.TryWrite((id, request));
        return id;
    }

    public bool TryGet(Guid id, out SyncStatus? status) => statuses.TryGetValue(id, out status);

    public async ValueTask<(Guid Id, SyncRequest Request)> Dequeue(CancellationToken cancellationToken) => await jobs.Reader.ReadAsync(cancellationToken);

    public void MarkRunning(Guid id)
    {
        if (statuses.TryGetValue(id, out var status))
        {
            statuses[id] = status with { Status = "running" };
        }
    }

    public void MarkCompleted(Guid id, IReadOnlyList<ImportSummary> imports, string? error = null)
    {
        if (statuses.TryGetValue(id, out var status))
        {
            var failed = imports.Any(item => item.Errors.Count > 0 || item.Status is AdapterCapabilityStatus.NotFound or AdapterCapabilityStatus.PermissionDenied or AdapterCapabilityStatus.UnsupportedVersion);
            statuses[id] = status with
            {
                Status = error is not null ? "failed" : failed && imports.Any(item => item.ImportedEventCount > 0) ? "partial" : failed ? "failed" : "completed",
                CompletedAtUtc = DateTimeOffset.UtcNow,
                Imports = imports,
                Error = error
            };
        }
    }
}

public sealed class SyncWorker : BackgroundService
{
    private readonly SyncJobService jobs;
    private readonly SourceAdapterRegistry adapters;
    private readonly DashboardStore store;
    private readonly IOptions<ApiOptions> options;

    public SyncWorker(SyncJobService jobs, SourceAdapterRegistry adapters, DashboardStore store, IOptions<ApiOptions> options)
    {
        this.jobs = jobs;
        this.adapters = adapters;
        this.store = store;
        this.options = options;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var job = await jobs.Dequeue(stoppingToken);
            jobs.MarkRunning(job.Id);
            var summaries = new List<ImportSummary>();
            try
            {
                var discovery = new SourceDiscoveryOptions(
                    SourceAdapterRegistry.CurrentPlatform,
                    options.Value.SourceHome ?? SourceAdapterRegistry.UserHome,
                    options.Value.SourceAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
                var paths = job.Request.Paths?.Where(static path => !string.IsNullOrWhiteSpace(path)).ToArray() ?? [];
                var sources = new List<(ILogSourceAdapter Adapter, string Path)>();
                if (!string.IsNullOrWhiteSpace(job.Request.Adapter))
                {
                    var adapter = adapters.Get(job.Request.Adapter);
                    sources.AddRange(paths.Select(path => (adapter, path)));
                    if (paths.Length == 0)
                    {
                        sources.AddRange(adapter.DiscoverPaths(discovery)
                            .Where(static candidate => candidate.Exists)
                            .Select(candidate => (adapter, candidate.Path)));
                    }
                }
                else if (paths.Length == 0)
                {
                    sources.AddRange(adapters.DiscoverAutoSources(discovery));
                }
                else
                {
                    foreach (var path in paths)
                    {
                        var adapter = adapters.IdentifyAutoPath(path);
                        if (adapter is null)
                        {
                            summaries.Add(new ImportSummary(Guid.NewGuid().ToString("N"), 0, 0, 0, [new ParseError(0, "Source adapter could not identify custom path")], AdapterCapabilityStatus.UnsupportedVersion));
                        }
                        else
                        {
                            sources.Add((adapter, path));
                        }
                    }
                }

                foreach (var source in sources)
                {
                    foreach (var file in ExpandSupportedFiles(source.Path, summaries))
                    {
                        summaries.Add(store.Read(connection => new ImportService(connection).Import(Guid.NewGuid().ToString("N"), file, source.Adapter, file, job.Request.WorkspaceId, job.Request.OwnerId)));
                    }
                }

                jobs.MarkCompleted(job.Id, summaries);
            }
            catch (Exception exception)
            {
                jobs.MarkCompleted(job.Id, summaries, exception.Message);
            }
        }
    }

    private static IEnumerable<string> ExpandSupportedFiles(string path, List<ImportSummary> summaries)
    {
        if (File.Exists(path))
        {
            if (IsSupportedExtension(path))
            {
                yield return path;
            }

            yield break;
        }

        if (!Directory.Exists(path))
        {
            summaries.Add(new ImportSummary(Guid.NewGuid().ToString("N"), 0, 0, 0, [new ParseError(0, "Source path was not found")], AdapterCapabilityStatus.NotFound));
            yield break;
        }

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories)
                .Where(IsSupportedExtension)
                .OrderBy(static item => item, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (UnauthorizedAccessException)
        {
            summaries.Add(new ImportSummary(Guid.NewGuid().ToString("N"), 0, 0, 0, [new ParseError(0, "Source path permission was denied")], AdapterCapabilityStatus.PermissionDenied));
            yield break;
        }

        foreach (var file in files)
        {
            yield return file;
        }
    }

    private static bool IsSupportedExtension(string path) => Path.GetExtension(path).ToLowerInvariant() is ".json" or ".jsonl" or ".ndjson" or ".csv";
}

public sealed record DateRange(DateTimeOffset FromUtc, DateTimeOffset ToUtc, string TimeZoneId);

public static class DateRangeResolver
{
    public static DateRange Resolve(string? preset, string? from, string? to, string? timeZone)
    {
        var zone = FindTimeZone(timeZone);
        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone);
        var localTo = string.IsNullOrWhiteSpace(to) ? now.Date.AddDays(1) : DateTime.Parse(to, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal).Date.AddDays(1);
        var localFrom = string.IsNullOrWhiteSpace(from)
            ? localTo.AddDays(string.Equals(preset, "7d", StringComparison.OrdinalIgnoreCase) ? -7 : string.Equals(preset, "90d", StringComparison.OrdinalIgnoreCase) ? -90 : -30)
            : DateTime.Parse(from, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal).Date;
        return new DateRange(ToUtc(localFrom, zone), ToUtc(localTo, zone), zone.Id);
    }

    public static TimeZoneInfo FindTimeZone(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return TimeZoneInfo.Utc;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(value);
        }
        catch (TimeZoneNotFoundException)
        {
            var windowsId = value switch
            {
                "Asia/Taipei" => "Taipei Standard Time",
                "America/Los_Angeles" => "Pacific Standard Time",
                "America/New_York" => "Eastern Standard Time",
                _ => value
            };
            return TimeZoneInfo.FindSystemTimeZoneById(windowsId);
        }
    }

    private static DateTimeOffset ToUtc(DateTime local, TimeZoneInfo zone)
    {
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone));
    }
}

public sealed record PriceCatalogEntry(
    string Provider,
    string Model,
    string Mode,
    string TokenType,
    long MinimumInputTokens,
    long? MaximumInputTokens,
    decimal UsdPerMillionTokens,
    string SourceName,
    string SourceUrl,
    string EffectiveDate);

public static class BuiltInPricingCatalog
{
    public const string Version = "2026-07-31";

    private const string OpenAiUrl = "https://developers.openai.com/api/docs/pricing";
    private const string AnthropicUrl = "https://platform.claude.com/docs/en/about-claude/pricing";

    public static IReadOnlyList<PriceCatalogEntry> Entries { get; } =
    [
        ..OpenAiEntries(),
        ..AnthropicEntries()
    ];

    private static IEnumerable<PriceCatalogEntry> OpenAiEntries()
    {
        var models = new[]
        {
            new ModelRate("gpt-5.6-sol", "2026-07-09", 5m, 30m, 0.50m, 6.25m, 10m, 60m, 1m, 12.50m, 272000, 10m, 60m, 1m, 12.50m),
            new ModelRate("gpt-5.6-terra", "2026-07-09", 2.50m, 15m, 0.25m, 3.125m, 5m, 30m, 0.50m, 6.25m, 272000, 5m, 30m, 0.50m, 6.25m),
            new ModelRate("gpt-5.6-luna", "2026-07-09", 1m, 6m, 0.10m, 1.25m, 2m, 12m, 0.20m, 2.50m, 272000, 2m, 12m, 0.20m, 2.50m),
            new ModelRate("gpt-5.6-terra", "2026-07-31", 2m, 12m, 0.20m, 2.50m, 4m, 18m, 0.40m, 5m, 272000, 4m, 24m, 0.40m, 5m),
            new ModelRate("gpt-5.6-luna", "2026-07-31", 0.20m, 1.20m, 0.02m, 0.25m, 0.40m, 1.80m, 0.04m, 0.50m, 272000, 0.40m, 2.40m, 0.04m, 0.50m),
            new ModelRate("gpt-5.5", "2026-04-23", 5m, 30m, 0.50m, null, 10m, 75m, 1.25m, null, 272000, 12.50m, 75m, 1.25m, null),
            new ModelRate("gpt-5.5-pro", "2026-04-23", 30m, 180m, null, null, 60m, 270m, null, null, 272000),
            new ModelRate("gpt-5.4", "2026-03-05", 2.50m, 15m, 0.25m, null, 5m, 22.50m, 0.50m, null, 272000, 5m, 30m, 0.50m, null),
            new ModelRate("gpt-5.4-mini", "2026-03-05", 0.75m, 4.50m, 0.075m, null, null, null, null, null, 272000, 1.50m, 9m, 0.15m, null),
            new ModelRate("gpt-5.4-nano", "2026-03-05", 0.20m, 1.25m, 0.02m, null, null, null, null, null, 272000),
            new ModelRate("gpt-5.4-pro", "2026-03-05", 30m, 180m, null, null, 60m, 270m, null, null, 272000)
        };

        foreach (var model in models)
        {
            foreach (var entry in ModelEntries("openai", model, model.EffectiveDate, OpenAiUrl, 272000)) yield return entry;
        }
    }

    private static IEnumerable<PriceCatalogEntry> AnthropicEntries()
    {
        var models = new[]
        {
            new AnthropicRate("claude-fable-5", 10m, 50m),
            new AnthropicRate("claude-mythos-5", 10m, 50m),
            new AnthropicRate("claude-opus-5", 5m, 25m),
            new AnthropicRate("claude-opus-4.8", 5m, 25m),
            new AnthropicRate("claude-opus-4.7", 5m, 25m),
            new AnthropicRate("claude-opus-4.6", 5m, 25m),
            new AnthropicRate("claude-opus-4.5", 5m, 25m),
            new AnthropicRate("claude-sonnet-4.6", 3m, 15m),
            new AnthropicRate("claude-sonnet-4.5", 3m, 15m),
            new AnthropicRate("claude-haiku-4.5", 1m, 5m)
        };

        foreach (var model in models)
        {
            foreach (var entry in AnthropicModelEntries(model.Model, model.Input, model.Output, "2026-07-28")) yield return entry;
        }

        foreach (var entry in AnthropicModelEntries("claude-sonnet-5", 2m, 10m, "2026-07-28")) yield return entry;
        foreach (var entry in AnthropicModelEntries("claude-sonnet-5", 3m, 15m, "2026-09-01")) yield return entry;

        foreach (var entry in AnthropicModelEntries("claude-sonnet-4", 3m, 15m, "2025-05-22", 200000, includeBatch: false)) yield return entry;
        foreach (var entry in AnthropicModelEntries("claude-opus-4.1", 15m, 75m, "2025-05-22", 200000, includeBatch: false)) yield return entry;
        foreach (var entry in AnthropicModelEntries("claude-haiku-3.5", 0.80m, 4m, "2024-10-22", 200000, includeBatch: false)) yield return entry;
    }

    private static IEnumerable<PriceCatalogEntry> ModelEntries(string provider, ModelRate model, string effectiveDate, string sourceUrl, long longContextThreshold)
    {
        foreach (var entry in TokenEntries(provider, model.Model, "standard", model.Input, model.Output, model.CachedInput, model.CacheWrite, 0, model.StandardMaximum, effectiveDate, sourceUrl)) yield return entry;
        foreach (var entry in TokenEntries(provider, model.Model, "batch", model.Input / 2, model.Output / 2, model.CachedInput / 2, model.CacheWrite / 2, 0, model.StandardMaximum, effectiveDate, sourceUrl)) yield return entry;
        foreach (var entry in TokenEntries(provider, model.Model, "flex", model.Input / 2, model.Output / 2, model.CachedInput / 2, model.CacheWrite / 2, 0, model.StandardMaximum, effectiveDate, sourceUrl)) yield return entry;

        if (model.LongInput is not null)
        {
            foreach (var entry in TokenEntries(provider, model.Model, "long-context-1m", model.LongInput.Value, model.LongOutput!.Value, model.LongCachedInput, model.LongCacheWrite, longContextThreshold, null, effectiveDate, sourceUrl)) yield return entry;
            foreach (var entry in TokenEntries(provider, model.Model, "batch-long-context-1m", model.LongInput.Value / 2, model.LongOutput.Value / 2, model.LongCachedInput / 2, model.LongCacheWrite / 2, longContextThreshold, null, effectiveDate, sourceUrl)) yield return entry;
        }

        if (model.FastInput is not null)
        {
            foreach (var entry in TokenEntries(provider, model.Model, PricingMode.Fast, model.FastInput.Value, model.FastOutput!.Value, model.FastCachedInput, model.FastCacheWrite, 0, model.StandardMaximum, effectiveDate, sourceUrl)) yield return entry;
        }
    }

    private static IEnumerable<PriceCatalogEntry> AnthropicModelEntries(string model, decimal input, decimal output, string effectiveDate, long? maximumInputTokens = null, bool includeBatch = true)
    {
        foreach (var entry in AnthropicTokenEntries(model, "standard", input, output, effectiveDate, maximumInputTokens)) yield return entry;
        if (includeBatch)
        {
            foreach (var entry in AnthropicTokenEntries(model, "batch", input / 2, output / 2, effectiveDate, maximumInputTokens)) yield return entry;
        }
    }

    private static IEnumerable<PriceCatalogEntry> AnthropicTokenEntries(string model, string mode, decimal input, decimal output, string effectiveDate, long? maximumInputTokens)
    {
        yield return new("anthropic", model, mode, "input", 0, maximumInputTokens, input, "Anthropic", AnthropicUrl, effectiveDate);
        yield return new("anthropic", model, mode, "cache-write-5m", 0, maximumInputTokens, input * 1.25m, "Anthropic", AnthropicUrl, effectiveDate);
        yield return new("anthropic", model, mode, "cache-write-1h", 0, maximumInputTokens, input * 2m, "Anthropic", AnthropicUrl, effectiveDate);
        yield return new("anthropic", model, mode, "cache-read", 0, maximumInputTokens, input * 0.1m, "Anthropic", AnthropicUrl, effectiveDate);
        yield return new("anthropic", model, mode, "output", 0, maximumInputTokens, output, "Anthropic", AnthropicUrl, effectiveDate);
    }

    private static IEnumerable<PriceCatalogEntry> TokenEntries(string provider, string model, string mode, decimal input, decimal output, decimal? cachedInput, decimal? cacheWrite, long minimumInputTokens, long? maximumInputTokens, string effectiveDate, string sourceUrl)
    {
        yield return new(provider, model, mode, "input", minimumInputTokens, maximumInputTokens, input, provider == "openai" ? "OpenAI" : "Anthropic", sourceUrl, effectiveDate);
        if (cachedInput is not null) yield return new(provider, model, mode, "cached-input", minimumInputTokens, maximumInputTokens, cachedInput.Value, provider == "openai" ? "OpenAI" : "Anthropic", sourceUrl, effectiveDate);
        if (cacheWrite is not null) yield return new(provider, model, mode, "cache-write", minimumInputTokens, maximumInputTokens, cacheWrite.Value, provider == "openai" ? "OpenAI" : "Anthropic", sourceUrl, effectiveDate);
        yield return new(provider, model, mode, "output", minimumInputTokens, maximumInputTokens, output, provider == "openai" ? "OpenAI" : "Anthropic", sourceUrl, effectiveDate);
    }

    private sealed record ModelRate(
        string Model,
        string EffectiveDate,
        decimal Input,
        decimal Output,
        decimal? CachedInput,
        decimal? CacheWrite,
        decimal? LongInput,
        decimal? LongOutput,
        decimal? LongCachedInput,
        decimal? LongCacheWrite,
        int StandardMaximum,
        decimal? FastInput = null,
        decimal? FastOutput = null,
        decimal? FastCachedInput = null,
        decimal? FastCacheWrite = null);

    private sealed record AnthropicRate(string Model, decimal Input, decimal Output);

    public static PricingSuggestionDto? Suggest(
        string provider,
        string model,
        string tokenType,
        string mode,
        long totalInputTokens)
    {
        var normalizedProvider = provider.Trim();
        var normalizedModel = model.Trim();
        var normalizedTokenType = TokenTypeNormalizer.Normalize(tokenType);
        var normalizedMode = string.IsNullOrWhiteSpace(mode) ? "standard" : PricingMode.Normalize(mode);
        var tokenCandidates = TokenTypeNormalizer.PricingVariants(normalizedTokenType).ToList();
        if (normalizedTokenType == "reasoning") tokenCandidates.Add("output");

        var candidates = Entries
            .Where(entry => string.Equals(entry.Provider, normalizedProvider, StringComparison.OrdinalIgnoreCase))
            .Where(entry => ModelsMatch(normalizedProvider, normalizedModel, entry.Model))
            .Where(entry => tokenCandidates.Contains(TokenTypeNormalizer.Normalize(entry.TokenType), StringComparer.OrdinalIgnoreCase))
            .Where(entry => string.Equals(PricingMode.Normalize(entry.Mode), normalizedMode, StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalizedMode, "standard", StringComparison.OrdinalIgnoreCase) && string.Equals(entry.Mode, "long-context-1m", StringComparison.OrdinalIgnoreCase))
            .Where(entry => totalInputTokens >= entry.MinimumInputTokens && (entry.MaximumInputTokens is null || totalInputTokens < entry.MaximumInputTokens))
            .OrderByDescending(entry => DateTimeOffset.Parse(entry.EffectiveDate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal))
            .ThenByDescending(entry => string.Equals(entry.Mode, normalizedMode, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(entry => entry.MinimumInputTokens)
            .ToArray();

        var candidate = candidates.FirstOrDefault();
        if (candidate is null) return null;

        var reasons = new List<string>();
        if (!string.Equals(candidate.Model, normalizedModel, StringComparison.OrdinalIgnoreCase)) reasons.Add($"模型 alias → {candidate.Model}");
        if (!string.Equals(candidate.TokenType, normalizedTokenType, StringComparison.OrdinalIgnoreCase)) reasons.Add($"token type → {candidate.TokenType}");
        if (!string.Equals(candidate.Mode, normalizedMode, StringComparison.OrdinalIgnoreCase)) reasons.Add($"模式 → {candidate.Mode}");
        if (DateTimeOffset.Parse(candidate.EffectiveDate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) > DateTimeOffset.UtcNow) reasons.Add($"採用最新價格 {candidate.EffectiveDate}");
        if (normalizedTokenType == "reasoning") reasons.Add("reasoning 以 output 價格估算");

        return new PricingSuggestionDto(
            candidate.Model,
            candidate.Mode,
            candidate.TokenType,
            candidate.MinimumInputTokens,
            candidate.MaximumInputTokens,
            candidate.UsdPerMillionTokens,
            candidate.EffectiveDate,
            candidate.SourceName,
            candidate.SourceUrl,
            reasons.Count == 0 ? "使用官方最新價格" : string.Join("；", reasons));
    }

    private static bool ModelsMatch(string provider, string actual, string catalog)
    {
        if (string.Equals(actual, catalog, StringComparison.OrdinalIgnoreCase)) return true;
        return string.Equals(provider, "anthropic", StringComparison.OrdinalIgnoreCase)
            && CompactModelName(actual).Equals(CompactModelName(catalog), StringComparison.OrdinalIgnoreCase);
    }

    private static string CompactModelName(string value) => TokenTypeNormalizer.Normalize(value).Replace("-", string.Empty).Replace(".", string.Empty);

    public static PriceCatalogEntry? Find(string provider, string model, string tokenType, DateTimeOffset atUtc, long totalInputTokens, string? mode = null)
    {
        var normalizedMode = PricingMode.Normalize(mode);
        return Entries.Where(item => string.Equals(item.Provider, provider, StringComparison.OrdinalIgnoreCase) &&
                                     string.Equals(item.Model, model, StringComparison.OrdinalIgnoreCase) &&
                                     TokenTypeNormalizer.PricingVariants(tokenType).Contains(TokenTypeNormalizer.Normalize(item.TokenType), StringComparer.OrdinalIgnoreCase) &&
                                     DateTimeOffset.TryParse(item.EffectiveDate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var effectiveFrom) &&
                                     effectiveFrom <= atUtc.ToUniversalTime() &&
                                     (string.IsNullOrWhiteSpace(normalizedMode) || string.Equals(PricingMode.Normalize(item.Mode), normalizedMode, StringComparison.OrdinalIgnoreCase)) &&
                                     totalInputTokens >= item.MinimumInputTokens &&
                                     (item.MaximumInputTokens is null || totalInputTokens < item.MaximumInputTokens))
            .OrderByDescending(item => DateTimeOffset.Parse(item.EffectiveDate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal))
            .ThenByDescending(item => item.MinimumInputTokens)
            .FirstOrDefault();
    }
}

public sealed class PricingResolver
{
    private readonly DashboardDataService data;

    public PricingResolver(DashboardDataService data) => this.data = data;

    public PriceCatalogEntry? Resolve(string provider, string model, string tokenType, DateTimeOffset eventUtc, long totalInputTokens, string? mode = null)
    {
        var normalizedProvider = provider.Trim();
        var normalizedModel = model.Trim();
        var allowedModes = string.IsNullOrWhiteSpace(mode)
            ? new[] { "standard", "long-context-1m" }
            : [PricingMode.Normalize(mode)];
        var variants = TokenTypeNormalizer.PricingVariants(tokenType);
        var custom = data.Query(
            """
            SELECT provider, model, mode, token_type, minimum_input_tokens, maximum_input_tokens,
                   usd_per_token, effective_from_utc, effective_to_utc
            FROM price_versions
            WHERE provider = $provider AND model = $model
              AND effective_from_utc <= $eventUtc
              AND (effective_to_utc IS NULL OR effective_to_utc > $eventUtc)
              AND minimum_input_tokens <= $totalInputTokens
              AND (maximum_input_tokens IS NULL OR $totalInputTokens < maximum_input_tokens);
            """,
            ("$provider", normalizedProvider),
            ("$model", normalizedModel),
            ("$eventUtc", eventUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
            ("$totalInputTokens", totalInputTokens))
            .Where(row => variants.Contains(String(row, "token_type"), StringComparer.OrdinalIgnoreCase) && allowedModes.Contains(PricingMode.Normalize(String(row, "mode")), StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(row => DateTimeOffset.Parse(String(row, "effective_from_utc"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind))
            .ThenByDescending(row => Long(row, "minimum_input_tokens"))
            .FirstOrDefault();
        if (custom is not null)
        {
            return new PriceCatalogEntry(
                String(custom, "provider"),
                String(custom, "model"),
                String(custom, "mode"),
                String(custom, "token_type"),
                Long(custom, "minimum_input_tokens"),
                custom["maximum_input_tokens"] is null ? null : Long(custom, "maximum_input_tokens"),
                decimal.Parse(String(custom, "usd_per_token"), CultureInfo.InvariantCulture) * 1_000_000m,
                "User override",
                string.Empty,
                String(custom, "effective_from_utc"));
        }

        return variants.SelectMany(variant => allowedModes.Select(candidateMode => BuiltInPricingCatalog.Find(normalizedProvider, normalizedModel, variant, eventUtc, totalInputTokens, candidateMode)))
            .Where(static item => item is not null)
            .OrderByDescending(static item => item!.MinimumInputTokens)
            .FirstOrDefault();
    }

    private static string String(Dictionary<string, object?> row, string name) => Convert.ToString(row[name], CultureInfo.InvariantCulture) ?? string.Empty;

    private static long Long(Dictionary<string, object?> row, string name) => Convert.ToInt64(row[name], CultureInfo.InvariantCulture);
}

public sealed class DashboardDataService
{
    private readonly DashboardStore store;

    public DashboardDataService(DashboardStore store) => this.store = store;

    public IReadOnlyList<Dictionary<string, object?>> Query(string sql, params (string Name, object Value)[] parameters)
    {
        return store.Read<IReadOnlyList<Dictionary<string, object?>>>(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var parameter in parameters)
            {
                command.Parameters.AddWithValue(parameter.Name, parameter.Value);
            }

            using var reader = command.ExecuteReader();
            var rows = new List<Dictionary<string, object?>>();
            while (reader.Read())
            {
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (var index = 0; index < reader.FieldCount; index++)
                {
                    row[reader.GetName(index)] = reader.IsDBNull(index) ? null : reader.GetValue(index);
                }

                rows.Add(row);
            }

            return rows;
        });
    }

    public void Execute(string sql, params (string Name, object Value)[] parameters)
    {
        store.Write(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var parameter in parameters)
            {
                command.Parameters.AddWithValue(parameter.Name, parameter.Value);
            }

            command.ExecuteNonQuery();
        });
    }

    public void Transaction(Action<SqliteConnection, SqliteTransaction> action)
    {
        store.Write(connection =>
        {
            using var transaction = connection.BeginTransaction();
            action(connection, transaction);
            transaction.Commit();
        });
    }

    public void AddPrice(PriceVersion version)
    {
        store.Read(connection =>
        {
            new HistoricalPricingService(connection).Add(version);
            return 0;
        });
    }

    public ImportSummary Import(string importId, string path, ILogSourceAdapter adapter, string? workspaceId, string? ownerId)
    {
        return store.Read(connection => new ImportService(connection).Import(importId, path, adapter, path, workspaceId, ownerId));
    }

    public void RebuildFts() => store.Write(FtsIndexingService.Rebuild);

    public IReadOnlyList<SearchResult> SearchFts(string query, int limit)
    {
        return store.Read<IReadOnlyList<SearchResult>>(connection =>
        {
            var terms = query.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var results = new Dictionary<string, SearchResult>(StringComparer.Ordinal);
            foreach (var term in terms.Length == 0 ? [query] : terms)
            {
                foreach (var result in FtsIndexingService.Search(connection, term, Math.Min(limit, 500)))
                {
                    results.TryAdd(result.ItemId, result);
                }
            }

            if (results.Count > 0)
            {
                return results.Values.ToArray();
            }

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT event_fingerprint, source_id, session_id, turn_id FROM sub_events WHERE prompt LIKE $query OR response LIKE $query OR tool LIKE $query OR subagent LIKE $query OR workflow LIKE $query OR model LIKE $query LIMIT $limit;";
            command.Parameters.AddWithValue("$query", $"%{query}%");
            command.Parameters.AddWithValue("$limit", limit);
            using var reader = command.ExecuteReader();
            var fallback = new List<SearchResult>();
            while (reader.Read())
            {
                fallback.Add(new SearchResult(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), 0));
            }

            return fallback;
        });
    }

    public byte[] Backup(bool includeContent = false)
    {
        return store.Read(connection =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"token-dashboard-export-{Guid.NewGuid():N}.db");
            try
            {
                byte[] bytes;
                using (var destination = new SqliteConnection($"Data Source={path};Pooling=False"))
                {
                    destination.Open();
                    connection.BackupDatabase(destination);
                    if (!includeContent)
                    {
                        using var scrub = destination.CreateCommand();
                        scrub.CommandText = "UPDATE sub_events SET prompt = '', response = '', payload = ''; UPDATE contents SET body = ''; UPDATE search_index SET prompt = '', response = '';";
                        scrub.ExecuteNonQuery();
                        using var vacuum = destination.CreateCommand();
                        vacuum.CommandText = "VACUUM;";
                        vacuum.ExecuteNonQuery();
                    }
                }

                bytes = File.ReadAllBytes(path);

                return bytes;
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        });
    }
}

public sealed class BudgetService
{
    private readonly DashboardDataService data;

    public BudgetService(DashboardDataService data) => this.data = data;

    public IReadOnlyList<BudgetDto> List() => data.Query("SELECT budget_id, name, amount_usd, period, from_date, to_date, project_id, tag, enabled FROM budgets ORDER BY name, budget_id;")
        .Select(ToDto).ToArray();

    public BudgetDto? Get(string id) => data.Query("SELECT budget_id, name, amount_usd, period, from_date, to_date, project_id, tag, enabled FROM budgets WHERE budget_id = $id;", ("$id", id)).Select(ToDto).FirstOrDefault();

    public BudgetDto Create(BudgetRequest request)
    {
        var id = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        data.Execute("""
            INSERT INTO budgets (budget_id, name, amount_usd, period, from_date, to_date, project_id, tag, enabled, created_at_utc, updated_at_utc)
            VALUES ($id, $name, $amount, $period, $fromDate, $toDate, $projectId, $tag, $enabled, $now, $now);
            """, ("$id", id), ("$name", request.Name.Trim()), ("$amount", request.AmountUsd.ToString(CultureInfo.InvariantCulture)), ("$period", request.Period.ToLowerInvariant()), ("$fromDate", request.FromDate ?? DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), ("$toDate", (object?)request.ToDate ?? DBNull.Value), ("$projectId", (object?)request.ProjectId ?? DBNull.Value), ("$tag", (object?)request.Tag ?? DBNull.Value), ("$enabled", request.Enabled ? 1 : 0), ("$now", now));
        return Get(id)!;
    }

    public BudgetDto? Update(string id, BudgetRequest request)
    {
        if (Get(id) is null) return null;
        data.Execute("""
            UPDATE budgets SET name = $name, amount_usd = $amount, period = $period, from_date = $fromDate, to_date = $toDate,
                project_id = $projectId, tag = $tag, enabled = $enabled, updated_at_utc = $now WHERE budget_id = $id;
            """, ("$id", id), ("$name", request.Name.Trim()), ("$amount", request.AmountUsd.ToString(CultureInfo.InvariantCulture)), ("$period", request.Period.ToLowerInvariant()), ("$fromDate", request.FromDate ?? DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), ("$toDate", (object?)request.ToDate ?? DBNull.Value), ("$projectId", (object?)request.ProjectId ?? DBNull.Value), ("$tag", (object?)request.Tag ?? DBNull.Value), ("$enabled", request.Enabled ? 1 : 0), ("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)));
        return Get(id);
    }

    public bool Delete(string id)
    {
        var before = Get(id) is not null;
        if (before) data.Execute("DELETE FROM budgets WHERE budget_id = $id;", ("$id", id));
        return before;
    }

    private static BudgetDto ToDto(Dictionary<string, object?> row) => new(
        Convert.ToString(row["budget_id"], CultureInfo.InvariantCulture) ?? string.Empty,
        Convert.ToString(row["name"], CultureInfo.InvariantCulture) ?? string.Empty,
        decimal.Parse(Convert.ToString(row["amount_usd"], CultureInfo.InvariantCulture) ?? "0", CultureInfo.InvariantCulture),
        Convert.ToString(row["period"], CultureInfo.InvariantCulture) ?? "monthly",
        Convert.ToString(row["from_date"], CultureInfo.InvariantCulture) ?? string.Empty,
        row["to_date"] is null ? null : Convert.ToString(row["to_date"], CultureInfo.InvariantCulture),
        row["project_id"] is null ? null : Convert.ToString(row["project_id"], CultureInfo.InvariantCulture),
        row["tag"] is null ? null : Convert.ToString(row["tag"], CultureInfo.InvariantCulture),
        Convert.ToInt32(row["enabled"], CultureInfo.InvariantCulture) != 0);
}

public sealed class PricingService
{
    private readonly DashboardDataService data;

    public PricingService(DashboardDataService data) => this.data = data;

    public int OverrideCount => Convert.ToInt32(data.Query("SELECT COUNT(*) AS count FROM price_versions;").Single()["count"], CultureInfo.InvariantCulture);

    public IReadOnlyList<PricingEntryDto> List()
    {
        var builtIn = BuiltInPricingCatalog.Entries.Select(static entry => new PricingEntryDto(
            entry.Provider,
            entry.Model,
            entry.Mode,
            entry.TokenType,
            entry.MinimumInputTokens,
            entry.MaximumInputTokens,
            entry.UsdPerMillionTokens,
            entry.EffectiveDate,
            null,
            entry.SourceName,
            entry.SourceUrl,
            false,
            1,
            entry.EffectiveDate,
            BuiltInPricingCatalog.Version,
            "official"));
        var overrides = data.Query("""
            SELECT provider, model, mode, token_type, minimum_input_tokens, maximum_input_tokens,
                   usd_per_token, effective_from_utc, effective_to_utc, source_name, source_url,
                   override_version, created_at_utc, catalog_version, source_kind
            FROM price_versions
            ORDER BY effective_from_utc DESC;
            """).Select(static row => new PricingEntryDto(
            String(row, "provider"),
            String(row, "model"),
            String(row, "mode"),
            String(row, "token_type"),
            Long(row, "minimum_input_tokens"),
            row["maximum_input_tokens"] is null ? null : Long(row, "maximum_input_tokens"),
            decimal.Parse(String(row, "usd_per_token"), CultureInfo.InvariantCulture) * 1_000_000m,
            String(row, "effective_from_utc"),
            row["effective_to_utc"] is null ? null : String(row, "effective_to_utc"),
            string.IsNullOrWhiteSpace(String(row, "source_name")) ? "User override" : String(row, "source_name"),
            String(row, "source_url"),
            true,
            row["override_version"] is null ? 1 : Convert.ToInt32(row["override_version"], CultureInfo.InvariantCulture),
            String(row, "created_at_utc"),
            String(row, "catalog_version"),
            string.IsNullOrWhiteSpace(String(row, "source_kind")) ? "local-override" : String(row, "source_kind")));
        return builtIn.Concat(overrides).ToArray();
    }

    public PricingEntryDto Add(PriceWriteRequest request)
    {
        var mode = string.IsNullOrWhiteSpace(request.Mode) ? "standard" : request.Mode.Trim();
        var from = (request.EffectiveFromUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var version = new PriceVersion(
            request.Provider,
            request.Model,
            mode,
            TokenType.Create(request.TokenType),
            request.UsdPerMillionTokens / 1_000_000m,
            request.MinimumInputTokens,
            request.MaximumInputTokens,
            from,
            request.EffectiveToUtc,
            null,
            null);
        data.AddPrice(version);
        var sourceName = request.SourceName ?? "User override";
        var sourceUrl = request.SourceUrl ?? string.Empty;
        data.Execute("UPDATE price_versions SET source_name = $sourceName, source_url = $sourceUrl WHERE price_version_id = $id;", ("$sourceName", sourceName), ("$sourceUrl", sourceUrl), ("$id", StableId(version)));
        return new PricingEntryDto(
            version.Provider,
            version.Model,
            version.Mode,
            version.TokenType.Value,
            version.MinimumInputTokens,
            version.MaximumInputTokens,
            request.UsdPerMillionTokens,
            from.ToString("O", CultureInfo.InvariantCulture),
            request.EffectiveToUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            sourceName,
            sourceUrl,
            true);
    }

    public int Deactivate(PriceDeactivateRequest request)
    {
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        var mode = string.IsNullOrWhiteSpace(request.Mode) ? null : request.Mode;
        var rows = data.Query("SELECT price_version_id FROM price_versions WHERE provider = $provider AND model = $model AND token_type = $tokenType AND effective_to_utc IS NULL AND ($mode IS NULL OR mode = $mode);", ("$provider", request.Provider), ("$model", request.Model), ("$tokenType", TokenTypeNormalizer.Normalize(request.TokenType)), ("$mode", (object?)mode ?? DBNull.Value));
        foreach (var row in rows)
        {
            data.Execute("UPDATE price_versions SET effective_to_utc = $now WHERE price_version_id = $id;", ("$now", now), ("$id", String(row, "price_version_id")));
        }

        return rows.Count;
    }

    private static string String(Dictionary<string, object?> row, string name) => Convert.ToString(row[name], CultureInfo.InvariantCulture) ?? string.Empty;

    private static long Long(Dictionary<string, object?> row, string name) => Convert.ToInt64(row[name], CultureInfo.InvariantCulture);

    private static string StableId(PriceVersion version) => string.Join(
        ":",
        version.Provider,
        version.Model,
        version.Mode,
        version.TokenType.Value,
        version.MinimumInputTokens.ToString(CultureInfo.InvariantCulture),
        version.MaximumInputTokens?.ToString(CultureInfo.InvariantCulture) ?? "*",
        version.EffectiveFromUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
}

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
            var value when value.Contains("application support/claude", StringComparison.Ordinal) || value.Contains("appdata/claude", StringComparison.Ordinal) => SourceAdapterKind.ClaudeCodeApp,
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
    public const string Version = "2026-07-26";

    private const string OpenAiUrl = "https://developers.openai.com/api/docs/models/gpt-5.4";
    private const string AnthropicUrl = "https://docs.anthropic.com/en/docs/about-claude/pricing";

    public static IReadOnlyList<PriceCatalogEntry> Entries { get; } =
    [
        new("openai", "gpt-5.4", "standard", "input", 0, 272000, 2.50m, "OpenAI", OpenAiUrl, "2026-03-05"),
        new("openai", "gpt-5.4", "standard", "cached-input", 0, 272000, 0.25m, "OpenAI", OpenAiUrl, "2026-03-05"),
        new("openai", "gpt-5.4", "standard", "output", 0, 272000, 15m, "OpenAI", OpenAiUrl, "2026-03-05"),
        new("openai", "gpt-5.4", "long-context-1m", "input", 272000, null, 5m, "OpenAI", OpenAiUrl, "2026-03-05"),
        new("openai", "gpt-5.4", "long-context-1m", "cached-input", 272000, null, 0.25m, "OpenAI", OpenAiUrl, "2026-03-05"),
        new("openai", "gpt-5.4", "long-context-1m", "output", 272000, null, 22.5m, "OpenAI", OpenAiUrl, "2026-03-05"),
        new("anthropic", "claude-sonnet-4", "standard", "input", 0, 200000, 3m, "Anthropic", AnthropicUrl, "2025-05-22"),
        new("anthropic", "claude-sonnet-4", "standard", "cache-write-5m", 0, 200000, 3.75m, "Anthropic", AnthropicUrl, "2025-05-22"),
        new("anthropic", "claude-sonnet-4", "standard", "cache-write-1h", 0, 200000, 6m, "Anthropic", AnthropicUrl, "2025-05-22"),
        new("anthropic", "claude-sonnet-4", "standard", "cache-read", 0, 200000, 0.30m, "Anthropic", AnthropicUrl, "2025-05-22"),
        new("anthropic", "claude-sonnet-4", "standard", "output", 0, 200000, 15m, "Anthropic", AnthropicUrl, "2025-05-22"),
        new("anthropic", "claude-sonnet-4", "long-context-1m", "input", 200000, null, 6m, "Anthropic", AnthropicUrl, "2025-05-22"),
        new("anthropic", "claude-sonnet-4", "long-context-1m", "output", 200000, null, 22.5m, "Anthropic", AnthropicUrl, "2025-05-22")
    ];

    public static PriceCatalogEntry? Find(string provider, string model, string tokenType, DateTimeOffset atUtc, long totalInputTokens, string? mode = null)
    {
        return Entries.Where(item => string.Equals(item.Provider, provider, StringComparison.OrdinalIgnoreCase) &&
                                     string.Equals(item.Model, model, StringComparison.OrdinalIgnoreCase) &&
                                     TokenTypeNormalizer.PricingVariants(tokenType).Contains(TokenTypeNormalizer.Normalize(item.TokenType), StringComparer.OrdinalIgnoreCase) &&
                                     DateTimeOffset.TryParse(item.EffectiveDate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var effectiveFrom) &&
                                     effectiveFrom <= atUtc.ToUniversalTime() &&
                                     (string.IsNullOrWhiteSpace(mode) || string.Equals(item.Mode, mode, StringComparison.OrdinalIgnoreCase) || (string.Equals(mode, "standard", StringComparison.OrdinalIgnoreCase) && string.Equals(item.Mode, "standard", StringComparison.OrdinalIgnoreCase))) &&
                                     totalInputTokens >= item.MinimumInputTokens &&
                                     (item.MaximumInputTokens is null || totalInputTokens < item.MaximumInputTokens))
            .OrderByDescending(item => item.MinimumInputTokens)
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
            : [mode.Trim()];
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
            .Where(row => variants.Contains(String(row, "token_type"), StringComparer.OrdinalIgnoreCase) && allowedModes.Contains(String(row, "mode"), StringComparer.OrdinalIgnoreCase))
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

    public byte[] Backup()
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
            false));
        var overrides = data.Query("""
            SELECT provider, model, mode, token_type, minimum_input_tokens, maximum_input_tokens,
                   usd_per_token, effective_from_utc, effective_to_utc, source_name, source_url
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
            true));
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

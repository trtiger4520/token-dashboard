namespace TokenDashboard.Core;

public sealed record Session : ScopedContract
{
    public static readonly TimeSpan InactivityTimeout = TimeSpan.FromMinutes(30);

    public Session(
        string sessionId,
        string sourceId,
        DateTimeOffset startedAtUtc,
        DateTimeOffset lastActivityAtUtc,
        string sourceTimeZone,
        string? workspaceId = null,
        string? ownerId = null)
        : base(workspaceId, ownerId)
    {
        SessionId = ContractValidation.Required(sessionId, nameof(sessionId));
        SourceId = ContractValidation.Required(sourceId, nameof(sourceId));
        StartedAtUtc = ContractValidation.Utc(startedAtUtc, nameof(startedAtUtc));
        LastActivityAtUtc = ContractValidation.Utc(lastActivityAtUtc, nameof(lastActivityAtUtc));
        SourceTimeZone = ContractValidation.Required(sourceTimeZone, nameof(sourceTimeZone));

        if (LastActivityAtUtc < StartedAtUtc)
        {
            throw new ArgumentException("Last activity cannot precede session start", nameof(lastActivityAtUtc));
        }
    }

    public string SessionId { get; }

    public string Id => SessionId;

    public string SourceId { get; }

    public DateTimeOffset StartedAtUtc { get; }

    public DateTimeOffset LastActivityAtUtc { get; }

    public string SourceTimeZone { get; }

    public DateTimeOffset DerivedEndedAtUtc => DeriveEndUtc(LastActivityAtUtc);

    public static DateTimeOffset DeriveEndUtc(DateTimeOffset lastActivityAtUtc)
    {
        return ContractValidation.Utc(lastActivityAtUtc, nameof(lastActivityAtUtc)).Add(InactivityTimeout);
    }
}

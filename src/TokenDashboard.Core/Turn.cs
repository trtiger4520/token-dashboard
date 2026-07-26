namespace TokenDashboard.Core;

public sealed record Turn : ScopedContract
{
    public Turn(
        string turnId,
        string sessionId,
        int sequence,
        DateTimeOffset occurredAtUtc,
        string sourceTimeZone,
        TokenUsage tokenUsage,
        CacheUsage cacheUsage,
        EventFingerprint eventFingerprint,
        string? workspaceId = null,
        string? ownerId = null)
        : base(workspaceId, ownerId)
    {
        TurnId = ContractValidation.Required(turnId, nameof(turnId));
        SessionId = ContractValidation.Required(sessionId, nameof(sessionId));
        if (sequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence), sequence, "Sequence cannot be negative");
        }

        Sequence = sequence;
        OccurredAtUtc = ContractValidation.Utc(occurredAtUtc, nameof(occurredAtUtc));
        SourceTimeZone = ContractValidation.Required(sourceTimeZone, nameof(sourceTimeZone));
        TokenUsage = tokenUsage ?? throw new ArgumentNullException(nameof(tokenUsage));
        CacheUsage = cacheUsage ?? throw new ArgumentNullException(nameof(cacheUsage));
        EventFingerprint = eventFingerprint;
    }

    public string TurnId { get; }

    public string SessionId { get; }

    public int Sequence { get; }

    public DateTimeOffset OccurredAtUtc { get; }

    public string SourceTimeZone { get; }

    public TokenUsage TokenUsage { get; }

    public CacheUsage CacheUsage { get; }

    public EventFingerprint EventFingerprint { get; }
}

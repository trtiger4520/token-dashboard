namespace TokenDashboard.Core;

public sealed record SubEvent : ScopedContract
{
    public SubEvent(
        string subEventId,
        string sourceId,
        string eventType,
        DateTimeOffset occurredAtUtc,
        string sourceTimeZone,
        string payload,
        EventFingerprint eventFingerprint,
        string? sessionId = null,
        string? turnId = null,
        string? workspaceId = null,
        string? ownerId = null)
        : base(workspaceId, ownerId)
    {
        SubEventId = ContractValidation.Required(subEventId, nameof(subEventId));
        SourceId = ContractValidation.Required(sourceId, nameof(sourceId));
        EventType = ContractValidation.Required(eventType, nameof(eventType));
        OccurredAtUtc = ContractValidation.Utc(occurredAtUtc, nameof(occurredAtUtc));
        SourceTimeZone = ContractValidation.Required(sourceTimeZone, nameof(sourceTimeZone));
        Payload = payload ?? throw new ArgumentNullException(nameof(payload));
        EventFingerprint = eventFingerprint;
        SessionId = ContractValidation.Optional(sessionId, nameof(sessionId));
        TurnId = ContractValidation.Optional(turnId, nameof(turnId));
    }

    public string SubEventId { get; }

    public string SourceId { get; }

    public string EventType { get; }

    public DateTimeOffset OccurredAtUtc { get; }

    public string SourceTimeZone { get; }

    public string Payload { get; }

    public EventFingerprint EventFingerprint { get; }

    public string? SessionId { get; }

    public string? TurnId { get; }
}

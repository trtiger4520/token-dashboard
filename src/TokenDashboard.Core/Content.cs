namespace TokenDashboard.Core;

public sealed record Content : ScopedContract
{
    public Content(
        string contentId,
        string turnId,
        string role,
        string body,
        DateTimeOffset occurredAtUtc,
        string sourceTimeZone,
        string? workspaceId = null,
        string? ownerId = null)
        : base(workspaceId, ownerId)
    {
        ContentId = ContractValidation.Required(contentId, nameof(contentId));
        TurnId = ContractValidation.Required(turnId, nameof(turnId));
        Role = ContractValidation.Required(role, nameof(role));
        Body = body ?? throw new ArgumentNullException(nameof(body));
        OccurredAtUtc = ContractValidation.Utc(occurredAtUtc, nameof(occurredAtUtc));
        SourceTimeZone = ContractValidation.Required(sourceTimeZone, nameof(sourceTimeZone));
    }

    public string ContentId { get; }

    public string TurnId { get; }

    public string Role { get; }

    public string Body { get; }

    public DateTimeOffset OccurredAtUtc { get; }

    public string SourceTimeZone { get; }
}

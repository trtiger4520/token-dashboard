namespace TokenDashboard.Core;

public sealed record Source : ScopedContract
{
    public Source(
        string sourceId,
        string name,
        string kind,
        string sourceTimeZone,
        DateTimeOffset createdAtUtc,
        string? workspaceId = null,
        string? ownerId = null)
        : base(workspaceId, ownerId)
    {
        SourceId = ContractValidation.Required(sourceId, nameof(sourceId));
        Name = ContractValidation.Required(name, nameof(name));
        Kind = ContractValidation.Required(kind, nameof(kind));
        SourceTimeZone = ContractValidation.Required(sourceTimeZone, nameof(sourceTimeZone));
        CreatedAtUtc = ContractValidation.Utc(createdAtUtc, nameof(createdAtUtc));
    }

    public string SourceId { get; }

    public string Id => SourceId;

    public string Name { get; }

    public string Kind { get; }

    public string SourceTimeZone { get; }

    public DateTimeOffset CreatedAtUtc { get; }
}

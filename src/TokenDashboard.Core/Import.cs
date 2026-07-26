namespace TokenDashboard.Core;

public sealed record Import : ScopedContract
{
    public Import(
        string importId,
        string sourceId,
        DateTimeOffset importedAtUtc,
        string sourceTimeZone,
        EventFingerprint scanFingerprint,
        string? workspaceId = null,
        string? ownerId = null)
        : base(workspaceId, ownerId)
    {
        ImportId = ContractValidation.Required(importId, nameof(importId));
        SourceId = ContractValidation.Required(sourceId, nameof(sourceId));
        ImportedAtUtc = ContractValidation.Utc(importedAtUtc, nameof(importedAtUtc));
        SourceTimeZone = ContractValidation.Required(sourceTimeZone, nameof(sourceTimeZone));
        ScanFingerprint = scanFingerprint;
    }

    public string ImportId { get; }

    public string SourceId { get; }

    public DateTimeOffset ImportedAtUtc { get; }

    public string SourceTimeZone { get; }

    public EventFingerprint ScanFingerprint { get; }
}

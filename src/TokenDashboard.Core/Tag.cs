namespace TokenDashboard.Core;

public sealed record Tag : ScopedContract
{
    public Tag(
        string tagId,
        string key,
        string value,
        DateTimeOffset createdAtUtc,
        string? workspaceId = null,
        string? ownerId = null)
        : base(workspaceId, ownerId)
    {
        TagId = ContractValidation.Required(tagId, nameof(tagId));
        Key = ContractValidation.Required(key, nameof(key));
        Value = value ?? throw new ArgumentNullException(nameof(value));
        CreatedAtUtc = ContractValidation.Utc(createdAtUtc, nameof(createdAtUtc));
    }

    public string TagId { get; }

    public string Key { get; }

    public string Value { get; }

    public DateTimeOffset CreatedAtUtc { get; }
}

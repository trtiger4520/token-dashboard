namespace TokenDashboard.Core;

public abstract record ScopedContract
{
    protected ScopedContract(string? workspaceId, string? ownerId)
    {
        WorkspaceId = ContractValidation.Optional(workspaceId, nameof(workspaceId));
        OwnerId = ContractValidation.Optional(ownerId, nameof(ownerId));
    }

    public string? WorkspaceId { get; }

    public string? OwnerId { get; }
}

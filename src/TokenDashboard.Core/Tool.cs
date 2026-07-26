namespace TokenDashboard.Core;

public sealed record Tool : ScopedContract
{
    public Tool(
        string toolId,
        string turnId,
        string name,
        string input,
        DateTimeOffset startedAtUtc,
        string sourceTimeZone,
        string? output = null,
        DateTimeOffset? completedAtUtc = null,
        string? workspaceId = null,
        string? ownerId = null)
        : base(workspaceId, ownerId)
    {
        ToolId = ContractValidation.Required(toolId, nameof(toolId));
        TurnId = ContractValidation.Required(turnId, nameof(turnId));
        Name = ContractValidation.Required(name, nameof(name));
        Input = input ?? throw new ArgumentNullException(nameof(input));
        StartedAtUtc = ContractValidation.Utc(startedAtUtc, nameof(startedAtUtc));
        SourceTimeZone = ContractValidation.Required(sourceTimeZone, nameof(sourceTimeZone));
        Output = output;
        CompletedAtUtc = completedAtUtc is null
            ? null
            : ContractValidation.Utc(completedAtUtc.Value, nameof(completedAtUtc));

        if (CompletedAtUtc is not null && CompletedAtUtc < StartedAtUtc)
        {
            throw new ArgumentException("Tool completion cannot precede start", nameof(completedAtUtc));
        }
    }

    public string ToolId { get; }

    public string TurnId { get; }

    public string Name { get; }

    public string Input { get; }

    public string? Output { get; }

    public DateTimeOffset StartedAtUtc { get; }

    public DateTimeOffset? CompletedAtUtc { get; }

    public string SourceTimeZone { get; }
}

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TokenDashboard.Core;

public sealed record EventFingerprintInput
{
    public EventFingerprintInput(
        string sourceId,
        string eventType,
        DateTimeOffset occurredAtUtc,
        string sourceTimeZone,
        string payload,
        string? sessionId = null,
        string? turnId = null,
        int? sequence = null)
    {
        SourceId = ContractValidation.Required(sourceId, nameof(sourceId));
        EventType = ContractValidation.Required(eventType, nameof(eventType));
        OccurredAtUtc = ContractValidation.Utc(occurredAtUtc, nameof(occurredAtUtc));
        SourceTimeZone = ContractValidation.Required(sourceTimeZone, nameof(sourceTimeZone));
        Payload = payload ?? throw new ArgumentNullException(nameof(payload));
        SessionId = ContractValidation.Optional(sessionId, nameof(sessionId));
        TurnId = ContractValidation.Optional(turnId, nameof(turnId));
        if (sequence is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence), sequence, "Sequence cannot be negative");
        }

        Sequence = sequence;
    }

    public string SourceId { get; }

    public string EventType { get; }

    public DateTimeOffset OccurredAtUtc { get; }

    public string SourceTimeZone { get; }

    public string Payload { get; }

    public string? SessionId { get; }

    public string? TurnId { get; }

    public int? Sequence { get; }
}

public readonly record struct EventFingerprint
{
    public EventFingerprint(string value)
    {
        Value = ContractValidation.Required(value, nameof(value));
    }

    public string Value { get; }

    public string Hex => Value;

    public static EventFingerprint Create(EventFingerprintInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var canonical = string.Join(
            "|",
            Part(input.SourceId),
            Part(input.EventType),
            Part(input.OccurredAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
            Part(input.SourceTimeZone),
            Part(input.SessionId),
            Part(input.TurnId),
            Part(input.Sequence?.ToString(CultureInfo.InvariantCulture)),
            Part(input.Payload));

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return new EventFingerprint(Convert.ToHexString(digest).ToLowerInvariant());
    }

    public static EventFingerprint Create(
        string sourceId,
        string eventType,
        DateTimeOffset occurredAtUtc,
        string sourceTimeZone,
        string payload,
        string? sessionId = null,
        string? turnId = null,
        int? sequence = null)
    {
        return Create(new EventFingerprintInput(
            sourceId,
            eventType,
            occurredAtUtc,
            sourceTimeZone,
            payload,
            sessionId,
            turnId,
            sequence));
    }

    public override string ToString() => Value;

    private static string Part(string? value)
    {
        return value is null ? "-1:" : $"{value.Length}:{value}";
    }
}

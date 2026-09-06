namespace BackBurner.Server;

public sealed record IntegrationJobControlRecord
{
    public required Guid JobId { get; init; }
    public required string ControlTokenHash { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public enum IntegrationCancelOutcome
{
    NotFound,
    Canceled,
    AlreadyCanceled,
    TerminalConflict
}

public sealed record IntegrationCancelResult(
    IntegrationCancelOutcome Outcome,
    BackBurner.Contracts.IntegrationJobStatus? Job = null);

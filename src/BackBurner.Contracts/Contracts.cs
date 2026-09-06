namespace BackBurner.Contracts;

public static class BackBurnerCapabilities
{
    public const string PublicationFenceV1 = "protocol:publication-fence-v1";
}

public enum JobStatus
{
    Queued,
    Leased,
    Running,
    Paused,
    Succeeded,
    Failed,
    Canceled
}

public enum WorkerAvailability
{
    Available,
    HumanActive,
    Draining,
    GameWorkerReserved,
    Inhibited,
    PausedByOperator,
    Misconfigured,
    Offline
}

public enum WorkerActivityState
{
    None,
    HumanActive,
    IdleCooldown
}

public enum WorkerBlockingCategory
{
    None,
    HumanActivity,
    IdleCooldown,
    AgentWork,
    AgentReserved,
    SystemBusy,
    OperatorPaused,
    Configuration,
    Other
}

public enum WorkerActivityKind
{
    AvailableNoWork,
    HumanActivity,
    IdleCooldown,
    AgentWork,
    AgentReserved,
    SystemBusy,
    EncodingCpu,
    EncodingGpu,
    Upscaling,
    Draining,
    OperatorPaused,
    Misconfigured,
    Offline
}

public enum WorkerMode
{
    PersonalDesktop,
    SharedGameWorker,
    DedicatedRenderNode
}

public enum AttemptOutcome
{
    Running,
    Succeeded,
    EncoderFailed,
    NonRetryableFailure,
    Interrupted
}

public enum WorkerControlCommand
{
    None,
    Pause,
    Resume,
    StopAndRequeue
}

public sealed record HandBrakeSettings
{
    public string Container { get; init; } = "mkv";
    public string VideoEncoder { get; init; } = "x265";
    public decimal Quality { get; init; } = 20;
    public string EncoderPreset { get; init; } = "medium";
    public int? MaxWidth { get; init; }
    public int? MaxHeight { get; init; }
    public string AudioEncoder { get; init; } = "copy";
    public int? AudioBitrateKbps { get; init; }
    public bool AllAudio { get; init; } = true;
    public bool AllSubtitles { get; init; } = true;
    public bool IncludeChapterMarkers { get; init; } = true;
    public string[] ExtraArguments { get; init; } = [];
}

public sealed record PresetRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    public required HandBrakeSettings Settings { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record CreatePresetRequest(
    string Name,
    string Description,
    HandBrakeSettings Settings);

public sealed record CreateJobRequest
{
    public required string DisplayName { get; init; }
    public required string SourcePath { get; init; }
    public required string DestinationPath { get; init; }
    public string? PresetName { get; init; }
    public required HandBrakeSettings Settings { get; init; }
    public int MaxAttempts { get; init; } = 3;
    public string SubmittedBy { get; init; } = "web";
    public Guid? IdentityId { get; init; }
}

public sealed record CreateIntegrationJobRequest
{
    public required string DisplayName { get; init; }
    public required string SourcePath { get; init; }
    public required string DestinationPath { get; init; }
    public string? PresetName { get; init; }
    public required HandBrakeSettings Settings { get; init; }
    public int MaxAttempts { get; init; } = 3;
    public string ClientName { get; init; } = "integration";
}

public sealed record BatchItemRequest
{
    public required string DisplayName { get; init; }
    public required string SourcePath { get; init; }
    public required string DestinationPath { get; init; }
}

public sealed record CreateBatchRequest
{
    public required string DisplayName { get; init; }
    public required string SourceDirectory { get; init; }
    public string? PresetName { get; init; }
    public required HandBrakeSettings Settings { get; init; }
    public int MaxAttempts { get; init; } = 3;
    public string SubmittedBy { get; init; } = "web";
    public Guid? IdentityId { get; init; }
    public required BatchItemRequest[] Items { get; init; }
}

public sealed record BatchRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string DisplayName { get; init; }
    public required string SourceDirectory { get; init; }
    public string? PresetName { get; init; }
    public string SubmittedBy { get; init; } = "web";
    public Guid? IdentityId { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public required Guid[] JobIds { get; init; }
}

public sealed record DirectoryScanRequest(string DirectoryPath, bool Recursive = false);

public sealed record ScannedMediaFile(
    string LogicalPath,
    string RelativePath,
    string FileName,
    long SizeBytes);

public sealed record DirectoryScanResult(
    string DirectoryPath,
    IReadOnlyList<ScannedMediaFile> Files,
    bool Truncated);

public sealed record LeaseProof(Guid LeaseId, long Generation);

public sealed record JobAttempt
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string WorkerId { get; init; }
    public required Guid LeaseId { get; init; }
    public required long Generation { get; init; }
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    public AttemptOutcome Outcome { get; set; } = AttemptOutcome.Running;
    public string? Detail { get; set; }
}

public sealed record JobRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string DisplayName { get; init; }
    public required string SourcePath { get; init; }
    public required string DestinationPath { get; init; }
    public string? PresetName { get; init; }
    public required HandBrakeSettings Settings { get; init; }
    public required string[] RequiredCapabilities { get; init; }
    public Guid? BatchId { get; init; }
    public string SubmittedBy { get; init; } = "web";
    public Guid? IdentityId { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public JobStatus Status { get; set; } = JobStatus.Queued;
    public int MaxAttempts { get; init; } = 3;
    public int FailureCount { get; set; }
    public int InterruptionCount { get; set; }
    public DateTimeOffset? NextEligibleAt { get; set; }
    public string? AssignedWorkerId { get; set; }
    public Guid? LeaseId { get; set; }
    public long FencingGeneration { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public decimal Progress { get; set; }
    public int? EtaSeconds { get; set; }
    public string? LastError { get; set; }
    public long? OutputBytes { get; set; }
    public DateTimeOffset? PublicationAuthorizedAt { get; set; }
    public DateTimeOffset? CanceledAt { get; set; }
    public string? CancellationReason { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<JobAttempt> Attempts { get; init; } = [];
}

public sealed record IntegrationJobStatus
{
    public string ApiVersion { get; init; } = "v1";
    public required Guid JobId { get; init; }
    public required string DisplayName { get; init; }
    public required JobStatus Status { get; init; }
    public required string[] RequiredCapabilities { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? NextEligibleAt { get; init; }
    public string? AssignedWorkerId { get; init; }
    public decimal Progress { get; init; }
    public int? EtaSeconds { get; init; }
    public int FailureCount { get; init; }
    public int MaxAttempts { get; init; }
    public int InterruptionCount { get; init; }
    public string? LastError { get; init; }
    public long? OutputBytes { get; init; }
    public DateTimeOffset? CanceledAt { get; init; }
    public string? CancellationReason { get; init; }
    public bool CancellationAllowed { get; init; }
    public bool IsTerminal { get; init; }
}

public sealed record IntegrationJobCreatedResponse
{
    public string ApiVersion { get; init; } = "v1";
    public required Guid JobId { get; init; }
    public required string ControlToken { get; init; }
    public required string StatusPath { get; init; }
    public required string CancelPath { get; init; }
    public required IntegrationJobStatus Job { get; init; }
}

public sealed record IntegrationJobSummary
{
    public required Guid JobId { get; init; }
    public required string DisplayName { get; init; }
    public required JobStatus Status { get; init; }
    public required string[] RequiredCapabilities { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? NextEligibleAt { get; init; }
    public string? AssignedWorkerId { get; init; }
    public decimal Progress { get; init; }
    public int? EtaSeconds { get; init; }
    public required string SubmittedBy { get; init; }
}

public sealed record IntegrationWorkerSummary
{
    public required string WorkerId { get; init; }
    public required string DisplayName { get; init; }
    public WorkerMode Mode { get; init; }
    public WorkerAvailability Availability { get; init; }
    public WorkerBlockingCategory BlockingCategory { get; init; }
    public required string AvailabilityReason { get; init; }
    public DateTimeOffset? ReadyAt { get; init; }
    public required DateTimeOffset LastSeenAt { get; init; }
    public required string[] Capabilities { get; init; }
    public IntegrationJobSummary? ActiveJob { get; init; }
}

public sealed record IntegrationJobCounts(
    int Queued,
    int Leased,
    int Running,
    int Paused,
    int Succeeded,
    int Failed,
    int Canceled);

public sealed record IntegrationWorkerCounts(
    int Total,
    int Available,
    int Working,
    int Offline);

public sealed record IntegrationSystemStatus
{
    public string ApiVersion { get; init; } = "v1";
    public required DateTimeOffset At { get; init; }
    public required IntegrationJobCounts Jobs { get; init; }
    public required IntegrationWorkerCounts Workers { get; init; }
    public required IReadOnlyDictionary<string, int> AvailableCapabilities { get; init; }
    public required IReadOnlyList<IntegrationJobSummary> Queue { get; init; }
    public required IReadOnlyList<IntegrationWorkerSummary> WorkerDetails { get; init; }
}

public sealed record WorkerHeartbeat
{
    public required string WorkerId { get; init; }
    public required string DisplayName { get; init; }
    public WorkerMode Mode { get; init; } = WorkerMode.PersonalDesktop;
    public WorkerAvailability Availability { get; init; }
    public WorkerActivityState ActivityState { get; init; }
    public WorkerBlockingCategory BlockingCategory { get; init; }
    public string AvailabilityReason { get; init; } = "";
    public DateTimeOffset? ReadyAt { get; init; }
    public string[] Capabilities { get; init; } = [];
    public Dictionary<string, string> Profile { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Guid? ActiveJobId { get; init; }
    public LeaseProof? Lease { get; init; }
}

public sealed record WorkerRecord
{
    public required string WorkerId { get; init; }
    public required string DisplayName { get; set; }
    public WorkerMode Mode { get; set; } = WorkerMode.PersonalDesktop;
    public WorkerAvailability Availability { get; set; }
    public WorkerActivityState ActivityState { get; set; }
    public WorkerBlockingCategory BlockingCategory { get; set; }
    public string AvailabilityReason { get; set; } = "";
    public DateTimeOffset? ReadyAt { get; set; }
    public string[] Capabilities { get; set; } = [];
    public Dictionary<string, string> Profile { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? ActiveJobId { get; set; }
    public Guid? OwnerIdentityId { get; set; }
}

public sealed record UserIdentityRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string DisplayName { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record CreateIdentityRequest(string DisplayName);

public sealed record SetWorkerOwnerRequest(Guid? IdentityId);

public sealed record WorkerActivityRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string WorkerId { get; init; }
    public WorkerActivityKind Kind { get; init; }
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastObservedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EndedAt { get; set; }
    public string Reason { get; set; } = "";
    public Guid? JobId { get; init; }
}

public sealed record ClaimRequest(string WorkerId);

public sealed record JobLease(JobRecord Job, LeaseProof Lease, DateTimeOffset ExpiresAt);

public sealed record ProgressReport
{
    public required string WorkerId { get; init; }
    public required LeaseProof Lease { get; init; }
    public decimal Progress { get; init; }
    public int? EtaSeconds { get; init; }
    public bool IsPaused { get; init; }
}

public sealed record CompletionReport
{
    public required string WorkerId { get; init; }
    public required LeaseProof Lease { get; init; }
    public long OutputBytes { get; init; }
}

public sealed record PublicationAuthorizationRequest
{
    public required string WorkerId { get; init; }
    public required LeaseProof Lease { get; init; }
}

public sealed record FailureReport
{
    public required string WorkerId { get; init; }
    public required LeaseProof Lease { get; init; }
    public required string Error { get; init; }
    public int? ExitCode { get; init; }
    public bool Retryable { get; init; } = true;
    public bool ConsumesAttempt { get; init; } = true;
}

public sealed record InterruptionReport
{
    public required string WorkerId { get; init; }
    public required LeaseProof Lease { get; init; }
    public required string Reason { get; init; }
}

public sealed record CoordinatorEvent
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
    public required string Type { get; init; }
    public required string Message { get; init; }
    public Guid? JobId { get; init; }
    public string? WorkerId { get; init; }
    public Guid? IdentityId { get; init; }
}

public sealed record DashboardSnapshot(
    IReadOnlyList<JobRecord> Jobs,
    IReadOnlyList<BatchRecord> Batches,
    IReadOnlyList<PresetRecord> Presets,
    IReadOnlyList<WorkerRecord> Workers,
    IReadOnlyList<CoordinatorEvent> Events,
    IReadOnlyList<UserIdentityRecord> Identities,
    IReadOnlyList<WorkerActivityRecord> WorkerActivities);

public sealed record ApiError(string Error);

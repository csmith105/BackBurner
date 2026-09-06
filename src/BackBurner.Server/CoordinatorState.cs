using BackBurner.Contracts;

namespace BackBurner.Server;

public sealed class CoordinatorState
{
    public List<JobRecord> Jobs { get; init; } = [];
    public List<BatchRecord> Batches { get; init; } = [];
    public List<PresetRecord> Presets { get; init; } = [];
    public List<WorkerRecord> Workers { get; init; } = [];
    public List<CoordinatorEvent> Events { get; init; } = [];
    public List<UserIdentityRecord> Identities { get; init; } = [];
    public List<WorkerActivityRecord> WorkerActivities { get; init; } = [];
    public List<IntegrationJobControlRecord> IntegrationJobControls { get; init; } = [];
}

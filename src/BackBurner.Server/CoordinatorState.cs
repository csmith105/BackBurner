using BackBurner.Contracts;

namespace BackBurner.Server;

public sealed class CoordinatorState
{
    public List<JobRecord> Jobs { get; init; } = [];
    public List<PresetRecord> Presets { get; init; } = [];
    public List<WorkerRecord> Workers { get; init; } = [];
    public List<CoordinatorEvent> Events { get; init; } = [];
}

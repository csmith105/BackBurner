using System.Text.Json;
using System.Text.Json.Serialization;

namespace BackBurner.Worker.Core;

public enum WorkerMode
{
    PersonalDesktop,
    SharedGameWorker,
    DedicatedRenderNode
}

public sealed record WorkerConfiguration
{
    public required string CoordinatorUrl { get; init; }
    public required string WorkerId { get; init; }
    public required string DisplayName { get; init; }
    public string WorkerApiKey { get; init; } = "";
    public string HandBrakePath { get; init; } = "HandBrakeCLI";
    public WorkerMode Mode { get; init; } = WorkerMode.PersonalDesktop;
    public int PollIntervalSeconds { get; init; } = 5;
    public int IdleThresholdSeconds { get; init; } = 900;
    public int QuietWindowSeconds { get; init; } = 900;
    public int PreflightSeconds { get; init; } = 30;
    public bool DetectWindowsHumanIdle { get; init; }
    public bool DetectCodexProcessActivity { get; init; } = true;
    public decimal CodexCpuBusyPercent { get; init; } = 1;
    public decimal SystemCpuBusyPercent { get; init; } = 20;
    public string? GameWorkerLeaseFile { get; init; }
    public string? GameWorkerQueueFile { get; init; }
    public string CodyWorkerBrokerPath { get; init; } = "/usr/local/bin/cody-workerctl";
    public string CodyWorkerProfile { get; init; } = "cpu";
    public int CodyWorkerLeaseTtlSeconds { get; init; } = 60;
    public int CodyWorkerRenewSeconds { get; init; } = 20;
    public string[] InhibitFiles { get; init; } = [];
    public string[] InhibitDirectories { get; init; } = [];
    public int CoordinatorLossStopSeconds { get; init; } = 20;
    public string[] Capabilities { get; init; } = [];
    public Dictionary<string, string> Profile { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Paths { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public static WorkerConfiguration Load(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Worker configuration was not found.", fullPath);
        }

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() }
        };
        var configuration = JsonSerializer.Deserialize<WorkerConfiguration>(File.ReadAllText(fullPath), options)
            ?? throw new InvalidOperationException("Worker configuration is empty or invalid.");
        configuration.Validate();
        return configuration;
    }

    private void Validate()
    {
        if (!Uri.TryCreate(CoordinatorUrl, UriKind.Absolute, out var coordinator) || coordinator.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("coordinatorUrl must be an absolute HTTP or HTTPS URL.");
        }
        if (string.IsNullOrWhiteSpace(WorkerId) || WorkerId.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')))
        {
            throw new InvalidOperationException("workerId is required and may contain only letters, digits, dash, underscore, and dot.");
        }
        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            throw new InvalidOperationException("displayName is required.");
        }
        if (PollIntervalSeconds is < 2 or > 30)
        {
            throw new InvalidOperationException("pollIntervalSeconds must be between 2 and 30.");
        }
        if (IdleThresholdSeconds is < 30 or > 86_400)
        {
            throw new InvalidOperationException("idleThresholdSeconds must be between 30 seconds and one day.");
        }
        if (QuietWindowSeconds is < 0 or > 86_400)
        {
            throw new InvalidOperationException("quietWindowSeconds must be between 0 and one day.");
        }
        if (PreflightSeconds is < 0 or > 300)
        {
            throw new InvalidOperationException("preflightSeconds must be between 0 and 300.");
        }
        if (CodexCpuBusyPercent is < 0 or > 100 || SystemCpuBusyPercent is < 0 or > 100)
        {
            throw new InvalidOperationException("CPU busy thresholds must be between 0 and 100 percent.");
        }
        if (CoordinatorLossStopSeconds is < 5 or > 120)
        {
            throw new InvalidOperationException("coordinatorLossStopSeconds must be between 5 and 120.");
        }
        if (Paths.Count == 0)
        {
            throw new InvalidOperationException("At least one logical path mapping is required.");
        }
        if (!Capabilities.Contains("handbrake", StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The first worker milestone requires the 'handbrake' capability.");
        }
        if ((GameWorkerLeaseFile is null) != (GameWorkerQueueFile is null))
        {
            throw new InvalidOperationException("gameWorkerLeaseFile and gameWorkerQueueFile must be configured together.");
        }
        if (Mode == WorkerMode.SharedGameWorker && GameWorkerLeaseFile is null)
        {
            throw new InvalidOperationException("SharedGameWorker mode requires the game-worker lease and queue files.");
        }
        if (Mode == WorkerMode.SharedGameWorker)
        {
            if (string.IsNullOrWhiteSpace(CodyWorkerBrokerPath))
            {
                throw new InvalidOperationException("SharedGameWorker mode requires codyWorkerBrokerPath.");
            }
            if (CodyWorkerProfile is not ("cpu" or "gpu"))
            {
                throw new InvalidOperationException("codyWorkerProfile must be cpu or gpu for a background tenant.");
            }
            if (CodyWorkerLeaseTtlSeconds != 60)
            {
                throw new InvalidOperationException("SharedGameWorker mode requires a 60-second broker lease.");
            }
            if (CodyWorkerRenewSeconds is < 10 or > 30 || CodyWorkerRenewSeconds >= CodyWorkerLeaseTtlSeconds)
            {
                throw new InvalidOperationException("codyWorkerRenewSeconds must be between 10 and 30 and shorter than the lease TTL.");
            }
        }
    }
}

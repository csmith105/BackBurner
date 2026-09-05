namespace BackBurner.Server;

public sealed class CoordinatorOptions
{
    public string DataFile { get; set; } = "data/backburner-state.json";
    public string AdminApiKey { get; set; } = "";
    public string WorkerApiKey { get; set; } = "";
    public int LeaseSeconds { get; set; } = 45;
    public int OfflineAfterSeconds { get; set; } = 30;
    public int RetryBaseDelaySeconds { get; set; } = 30;
    public Dictionary<string, string> SourceRoots { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int MaximumScanFiles { get; set; } = 5_000;
}

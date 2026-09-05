using System.Runtime.InteropServices;
using System.Text.Json;
using BackBurner.Contracts;

namespace BackBurner.Worker.Core;

public sealed record AvailabilitySnapshot(
    WorkerAvailability Availability,
    string Reason,
    DateTimeOffset? ReadyAt = null,
    WorkerActivityState ActivityState = WorkerActivityState.None,
    WorkerBlockingCategory BlockingCategory = WorkerBlockingCategory.None)
{
    public bool CanClaim => Availability == WorkerAvailability.Available;
    public bool RequiresImmediateYield => Availability is WorkerAvailability.GameWorkerReserved or WorkerAvailability.Inhibited or WorkerAvailability.Misconfigured;
}

public sealed class AvailabilityProbe
{
    private readonly WorkerConfiguration configuration;
    private readonly WorkerControl control;
    private readonly ProcessActivityMonitor activityMonitor = new();
    private DateTimeOffset lastBusyAt = DateTimeOffset.UtcNow;

    public AvailabilityProbe(WorkerConfiguration configuration, WorkerControl control)
    {
        this.configuration = configuration;
        this.control = control;
    }

    public AvailabilitySnapshot Check(bool jobRunning = false, string? ownedGameLeaseId = null)
    {
        if (control.IsOperatorPaused)
        {
            return new(WorkerAvailability.PausedByOperator, "Paused from the BackBurner notification-area menu.", BlockingCategory: WorkerBlockingCategory.OperatorPaused);
        }

        foreach (var inhibitFile in configuration.InhibitFiles)
        {
            if (File.Exists(Environment.ExpandEnvironmentVariables(inhibitFile)))
            {
                return new(WorkerAvailability.Inhibited, $"Higher-priority work is active ({Path.GetFileName(inhibitFile)}).", BlockingCategory: WorkerBlockingCategory.AgentWork);
            }
        }

        foreach (var configuredDirectory in configuration.InhibitDirectories)
        {
            var inhibitDirectory = Environment.ExpandEnvironmentVariables(configuredDirectory);
            if (!Directory.Exists(inhibitDirectory))
            {
                continue;
            }

            foreach (var marker in Directory.EnumerateFiles(inhibitDirectory, "*.json", SearchOption.TopDirectoryOnly))
            {
                var markerState = CheckInhibitMarker(marker);
                if (markerState is not null)
                {
                    return markerState;
                }
            }
        }

        if (configuration.GameWorkerLeaseFile is not null && configuration.GameWorkerQueueFile is not null)
        {
            var gameWorker = CheckGameWorker(configuration.GameWorkerLeaseFile, configuration.GameWorkerQueueFile, ownedGameLeaseId);
            if (gameWorker is not null)
            {
                return gameWorker;
            }
        }

        if (configuration.Mode == WorkerMode.DedicatedRenderNode)
        {
            return new(WorkerAvailability.Available, "Dedicated render node; desktop and background-activity gates are disabled.");
        }

        if (configuration.Mode == WorkerMode.SharedGameWorker)
        {
            return new(WorkerAvailability.Available, "Game-worker lease and queue are idle.");
        }

        if (configuration.DetectWindowsHumanIdle)
        {
            if (!OperatingSystem.IsWindows())
            {
                return new(WorkerAvailability.Misconfigured, "Windows human-idle detection was enabled on a non-Windows host.", BlockingCategory: WorkerBlockingCategory.Configuration);
            }
            var idle = GetWindowsIdleTime();
            if (idle < TimeSpan.FromSeconds(configuration.IdleThresholdSeconds))
            {
                lastBusyAt = DateTimeOffset.UtcNow;
                var readyAt = DateTimeOffset.UtcNow.AddSeconds(configuration.IdleThresholdSeconds - idle.TotalSeconds);
                if (idle < TimeSpan.FromSeconds(configuration.HumanActiveGraceSeconds))
                {
                    return new(WorkerAvailability.HumanActive, $"Human input was seen {Math.Floor(idle.TotalSeconds)} seconds ago.", readyAt, WorkerActivityState.HumanActive, WorkerBlockingCategory.HumanActivity);
                }
                return new(WorkerAvailability.HumanActive, $"No recent input; establishing {configuration.IdleThresholdSeconds / 60m:0.#} minutes of sustained idle time.", readyAt, WorkerActivityState.IdleCooldown, WorkerBlockingCategory.IdleCooldown);
            }
        }

        var activity = activityMonitor.Sample();
        if (configuration.DetectCodexProcessActivity && activity.IsPrimed && activity.CodexCpuPercent >= configuration.CodexCpuBusyPercent)
        {
            lastBusyAt = DateTimeOffset.UtcNow;
            return new(WorkerAvailability.Inhibited, $"Codex is using {activity.CodexCpuPercent:F1}% of host CPU; an agent may be active.", BlockingCategory: WorkerBlockingCategory.AgentWork);
        }
        if (!jobRunning && activity.IsPrimed && activity.SystemCpuPercent >= configuration.SystemCpuBusyPercent)
        {
            lastBusyAt = DateTimeOffset.UtcNow;
            return new(WorkerAvailability.Inhibited, $"The host is using {activity.SystemCpuPercent:F1}% CPU before encoding; waiting for a quiet window.", BlockingCategory: WorkerBlockingCategory.SystemBusy);
        }
        if (!activity.IsPrimed)
        {
            lastBusyAt = DateTimeOffset.UtcNow;
            return new(WorkerAvailability.Inhibited, "Establishing the initial activity baseline.", ActivityState: WorkerActivityState.IdleCooldown, BlockingCategory: WorkerBlockingCategory.IdleCooldown);
        }
        var quietFor = DateTimeOffset.UtcNow - lastBusyAt;
        if (!jobRunning && quietFor < TimeSpan.FromSeconds(configuration.QuietWindowSeconds))
        {
            var remaining = TimeSpan.FromSeconds(configuration.QuietWindowSeconds) - quietFor;
            return new(WorkerAvailability.Inhibited, "Input is idle; establishing a quiet-machine window.", DateTimeOffset.UtcNow.Add(remaining), WorkerActivityState.IdleCooldown, WorkerBlockingCategory.IdleCooldown);
        }

        return new(WorkerAvailability.Available, "All configured idle and exclusion checks passed.");
    }

    private static AvailabilitySnapshot? CheckInhibitMarker(string markerPath)
    {
        try
        {
            using var marker = ReadAtomicJson(markerPath);
            var root = marker.RootElement;
            var owner = root.TryGetProperty("owner", out var ownerValue) ? ownerValue.GetString() : null;
            var reason = root.TryGetProperty("reason", out var reasonValue) ? reasonValue.GetString() : null;
            if (!root.TryGetProperty("expires_at", out var expiryValue)
                || !DateTimeOffset.TryParse(expiryValue.GetString(), out var expiresAt))
            {
                return new(WorkerAvailability.Inhibited, $"Inhibit marker '{Path.GetFileName(markerPath)}' is malformed; refusing background work conservatively.", BlockingCategory: WorkerBlockingCategory.AgentWork);
            }
            if (expiresAt <= DateTimeOffset.UtcNow)
            {
                return null;
            }

            var description = string.IsNullOrWhiteSpace(reason) ? "higher-priority work" : reason;
            var source = string.IsNullOrWhiteSpace(owner) ? Path.GetFileName(markerPath) : owner;
            return new(WorkerAvailability.Inhibited, $"{description} is active ({source}); marker expires at {expiresAt:u}.", BlockingCategory: WorkerBlockingCategory.AgentWork);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new(WorkerAvailability.Inhibited, $"Inhibit marker '{Path.GetFileName(markerPath)}' could not be read safely: {exception.Message}", BlockingCategory: WorkerBlockingCategory.AgentWork);
        }
    }

    private static AvailabilitySnapshot? CheckGameWorker(string leaseFile, string queueFile, string? ownedLeaseId)
    {
        try
        {
            using var lease = ReadAtomicJson(leaseFile);
            using var queue = ReadAtomicJson(queueFile);
            var status = lease.RootElement.TryGetProperty("status", out var statusValue) ? statusValue.GetString() : null;
            var ownsCurrentLease = false;
            if (string.Equals(status, "leased", StringComparison.OrdinalIgnoreCase)
                && ownedLeaseId is not null
                && lease.RootElement.TryGetProperty("lease", out var leaseValue)
                && leaseValue.ValueKind == JsonValueKind.Object)
            {
                var currentLeaseId = leaseValue.TryGetProperty("lease_id", out var leaseIdValue) ? leaseIdValue.GetString() : null;
                var expiresAtValue = leaseValue.TryGetProperty("expires_at", out var expiryValue) ? expiryValue.GetString() : null;
                ownsCurrentLease = string.Equals(currentLeaseId, ownedLeaseId, StringComparison.OrdinalIgnoreCase)
                    && DateTimeOffset.TryParse(expiresAtValue, out var expiresAt)
                    && expiresAt > DateTimeOffset.UtcNow;
            }
            if (!string.Equals(status, "idle", StringComparison.OrdinalIgnoreCase) && !ownsCurrentLease)
            {
                return new(WorkerAvailability.GameWorkerReserved, $"Game-worker state is '{status ?? "unknown"}'.", BlockingCategory: WorkerBlockingCategory.AgentReserved);
            }
            if (!queue.RootElement.TryGetProperty("requests", out var requests) || requests.ValueKind != JsonValueKind.Array)
            {
                return new(WorkerAvailability.GameWorkerReserved, "Game-worker queue schema is unreadable; exclusion is conservative.", BlockingCategory: WorkerBlockingCategory.AgentReserved);
            }
            if (requests.GetArrayLength() > 0)
            {
                return new(WorkerAvailability.GameWorkerReserved, "Interactive game-development work is waiting in the FIFO queue.");
            }
            return ownsCurrentLease
                ? new(WorkerAvailability.Available, "BackBurner owns the current short broker lease and the development queue is empty.")
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new(WorkerAvailability.GameWorkerReserved, $"Game-worker state could not be read safely: {exception.Message}", BlockingCategory: WorkerBlockingCategory.AgentReserved);
        }
    }

    private static JsonDocument ReadAtomicJson(string path)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return JsonDocument.Parse(stream);
            }
            catch (Exception exception) when (exception is IOException or JsonException)
            {
                last = exception;
                Thread.Yield();
            }
        }
        throw last ?? new IOException($"Could not read '{path}'.");
    }

    private static TimeSpan GetWindowsIdleTime()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info))
        {
            throw new InvalidOperationException("Windows GetLastInputInfo failed.");
        }
        var elapsedMilliseconds = unchecked((uint)Environment.TickCount - info.Time);
        return TimeSpan.FromMilliseconds(elapsedMilliseconds);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo inputInfo);
}

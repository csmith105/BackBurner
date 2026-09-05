using BackBurner.Contracts;

namespace BackBurner.Worker.Core;

public sealed class WorkerControl
{
    private int command;
    private int operatorPaused;

    public bool IsOperatorPaused => Volatile.Read(ref operatorPaused) == 1;

    public void SetOperatorPaused(bool value) => Interlocked.Exchange(ref operatorPaused, value ? 1 : 0);
    public void RequestPause() => Interlocked.Exchange(ref command, (int)WorkerControlCommand.Pause);
    public void RequestResume() => Interlocked.Exchange(ref command, (int)WorkerControlCommand.Resume);
    public void RequestStopAndRequeue() => Interlocked.Exchange(ref command, (int)WorkerControlCommand.StopAndRequeue);
    public WorkerControlCommand ConsumeCommand() => (WorkerControlCommand)Interlocked.Exchange(ref command, (int)WorkerControlCommand.None);
}

public sealed record WorkerStatusSnapshot(
    WorkerAvailability Availability,
    string Reason,
    string? JobName,
    decimal Progress,
    int? EtaSeconds,
    bool IsPaused);

public sealed class WorkerRuntimeStatus
{
    private readonly object sync = new();
    private WorkerStatusSnapshot current = new(WorkerAvailability.Offline, "Starting.", null, 0, null, false);

    public event Action<WorkerStatusSnapshot>? Changed;

    public WorkerStatusSnapshot Current
    {
        get { lock (sync) return current; }
    }

    public void Update(WorkerStatusSnapshot value)
    {
        lock (sync) current = value;
        Changed?.Invoke(value);
    }
}

public interface IWorkerNotifier
{
    Task HumanReturnedAsync(WorkerStatusSnapshot status, CancellationToken cancellationToken);
    Task InformationAsync(string title, string message, CancellationToken cancellationToken);
}

public sealed class ConsoleWorkerNotifier : IWorkerNotifier
{
    public Task HumanReturnedAsync(WorkerStatusSnapshot status, CancellationToken cancellationToken)
    {
        Console.WriteLine($"Human activity detected. Finishing '{status.JobName}' by default; ETA {FormatEta(status.EtaSeconds)}. Use the Windows host for Pause or Stop & Requeue controls.");
        return Task.CompletedTask;
    }

    public Task InformationAsync(string title, string message, CancellationToken cancellationToken)
    {
        Console.WriteLine($"{title}: {message}");
        return Task.CompletedTask;
    }

    private static string FormatEta(int? seconds) => seconds is null ? "unknown" : TimeSpan.FromSeconds(seconds.Value).ToString("g");
}

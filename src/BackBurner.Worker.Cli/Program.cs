using BackBurner.Worker.Core;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: BackBurner.Worker.Cli <worker-config.json>");
    return 2;
}

try
{
    var configuration = WorkerConfiguration.Load(args[0]);
    var control = new WorkerControl();
    var status = new WorkerRuntimeStatus();
    status.Changed += current =>
    {
        var job = current.JobName is null ? "" : $" | {current.JobName} {current.Progress:P0}";
        Console.WriteLine($"[{DateTimeOffset.Now:T}] {current.Availability}: {current.Reason}{job}");
    };

    using var shutdown = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        shutdown.Cancel();
    };
    using var agent = new WorkerAgent(configuration, control, status, new ConsoleWorkerNotifier());
    await agent.RunAsync(shutdown.Token);
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

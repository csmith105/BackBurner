using System.ComponentModel;
using System.Text.Json;
using BackBurner.Contracts;

namespace BackBurner.Worker.Core;

public sealed class WorkerAgent : IDisposable
{
    private readonly WorkerConfiguration configuration;
    private readonly CoordinatorClient coordinator;
    private readonly LogicalPathResolver pathResolver;
    private readonly AvailabilityProbe availabilityProbe;
    private readonly WorkerControl control;
    private readonly WorkerRuntimeStatus runtimeStatus;
    private readonly IWorkerNotifier notifier;
    private readonly Action<string> log;
    private readonly CodyWorkerBroker? codyWorkerBroker;
    private Dictionary<string, string> profile = new(StringComparer.OrdinalIgnoreCase);
    private string? startupError;

    public WorkerAgent(
        WorkerConfiguration configuration,
        WorkerControl control,
        WorkerRuntimeStatus runtimeStatus,
        IWorkerNotifier notifier,
        Action<string>? log = null,
        HttpMessageHandler? httpHandler = null)
    {
        this.configuration = configuration;
        this.control = control;
        this.runtimeStatus = runtimeStatus;
        this.notifier = notifier;
        this.log = log ?? Console.WriteLine;
        coordinator = new CoordinatorClient(configuration, httpHandler);
        pathResolver = new LogicalPathResolver(configuration.Paths);
        availabilityProbe = new AvailabilityProbe(configuration, control);
        if (configuration.Mode == WorkerMode.SharedGameWorker)
        {
            codyWorkerBroker = new CodyWorkerBroker(configuration, this.log);
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        profile = ToolProbe.BuildHostProfile(configuration);
        try
        {
            profile = await ToolProbe.BuildProfileAsync(configuration, cancellationToken);
            log($"HandBrake ready: {profile["handBrakeVersion"]}");
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException or Win32Exception)
        {
            startupError = exception.Message;
            log($"Worker is misconfigured: {startupError}");
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (startupError is not null)
                {
                    var unavailable = new AvailabilitySnapshot(WorkerAvailability.Misconfigured, startupError);
                    runtimeStatus.Update(new(unavailable.Availability, unavailable.Reason, null, 0, null, false));
                    await coordinator.HeartbeatAsync(CreateHeartbeat(unavailable), cancellationToken);
                }
                else
                {
                    var availability = availabilityProbe.Check();
                    runtimeStatus.Update(new(availability.Availability, availability.Reason, null, 0, null, false));
                    await coordinator.HeartbeatAsync(CreateHeartbeat(availability), cancellationToken);
                    if (availability.CanClaim)
                    {
                        var lease = await coordinator.ClaimAsync(configuration.WorkerId, cancellationToken);
                        if (lease is not null)
                        {
                            if (!await CompletePreflightAsync(lease, cancellationToken))
                            {
                                continue;
                            }
                            CodyWorkerLease? codyLease = null;
                            try
                            {
                                if (codyWorkerBroker is not null)
                                {
                                    try
                                    {
                                        codyLease = await codyWorkerBroker.TryAcquireAsync(lease.Job, cancellationToken);
                                    }
                                    catch (Exception exception) when (exception is IOException or InvalidOperationException or JsonException or Win32Exception)
                                    {
                                        log($"Cody worker broker acquisition failed: {exception.Message}");
                                        await coordinator.InterruptAsync(lease.Job.Id, new InterruptionReport
                                        {
                                            WorkerId = configuration.WorkerId,
                                            Lease = lease.Lease,
                                            Reason = $"The Cody worker broker could not be used safely: {exception.Message}"
                                        }, cancellationToken);
                                        continue;
                                    }
                                    if (codyLease is null)
                                    {
                                        await coordinator.InterruptAsync(lease.Job.Id, new InterruptionReport
                                        {
                                            WorkerId = configuration.WorkerId,
                                            Lease = lease.Lease,
                                            Reason = "The Cody worker broker did not grant an immediate background lease; development work keeps priority."
                                        }, cancellationToken);
                                        continue;
                                    }
                                    log($"Acquired Cody worker broker generation {codyLease.Generation} for '{lease.Job.DisplayName}'.");
                                }
                                await ExecuteAsync(lease, codyLease, cancellationToken);
                            }
                            finally
                            {
                                if (codyLease is not null && codyWorkerBroker is not null)
                                {
                                    await ReleaseCodyLeaseAsync(codyWorkerBroker, codyLease);
                                }
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
            {
                log($"Coordinator cycle failed: {exception.Message}");
                runtimeStatus.Update(new(WorkerAvailability.Offline, "Coordinator is unreachable.", null, 0, null, false));
            }

            await DelaySafelyAsync(TimeSpan.FromSeconds(configuration.PollIntervalSeconds), cancellationToken);
        }
    }

    private async Task ExecuteAsync(JobLease claimed, CodyWorkerLease? codyLease, CancellationToken cancellationToken)
    {
        var job = claimed.Job;
        log($"Claimed '{job.DisplayName}' generation {claimed.Lease.Generation}.");
        string? partialDestination = null;
        HandBrakeSession? session = null;
        try
        {
            var source = pathResolver.Resolve(job.SourcePath);
            var destination = pathResolver.Resolve(job.DestinationPath);
            partialDestination = $"{destination}.{job.Id:N}.{claimed.Lease.LeaseId:N}.backburner-partial";
            if (!File.Exists(source))
            {
                await ReportNonRetryableFailure(job, claimed.Lease, $"Source file does not exist on this worker: {job.SourcePath}", cancellationToken);
                return;
            }
            if (File.Exists(destination))
            {
                await ReportNonRetryableFailure(job, claimed.Lease, $"Destination already exists; BackBurner will not overwrite it: {job.DestinationPath}", cancellationToken);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? throw new InvalidOperationException("Destination has no parent directory."));
            if (File.Exists(partialDestination)) File.Delete(partialDestination);
            if (!await coordinator.ProgressAsync(job.Id, CreateProgress(claimed.Lease, 0, null, false), cancellationToken))
            {
                log($"Lease for '{job.DisplayName}' was stale before encoding began.");
                return;
            }

            var handBrakeArguments = HandBrakeArgumentBuilder.Build(source, partialDestination, job.Settings);
            var startInfo = codyLease is null
                ? HandBrakeSession.CreateProcessStartInfo(configuration.HandBrakePath, handBrakeArguments)
                : codyWorkerBroker!.CreateHandBrakeStartInfo(codyLease, configuration.HandBrakePath, handBrakeArguments);
            session = HandBrakeSession.Start(startInfo);
            var humanReturnNotified = false;
            var pauseUntilIdle = false;
            var lastCoordinatorContact = DateTimeOffset.UtcNow;
            var nextCodyRenewal = DateTimeOffset.UtcNow.AddSeconds(configuration.CodyWorkerRenewSeconds);
            while (!session.Completion.IsCompleted)
            {
                var availability = availabilityProbe.Check(jobRunning: true, ownedGameLeaseId: codyLease?.LeaseId);
                var command = control.ConsumeCommand();
                if (availability.RequiresImmediateYield)
                {
                    await InterruptAsync(session, job, claimed.Lease, partialDestination, $"Yielded to higher-priority work: {availability.Reason}", cancellationToken);
                    return;
                }
                if (command == WorkerControlCommand.StopAndRequeue)
                {
                    await InterruptAsync(session, job, claimed.Lease, partialDestination, "Stopped by the local user and returned to the queue.", cancellationToken);
                    return;
                }
                if (codyLease is not null && codyWorkerBroker is not null && DateTimeOffset.UtcNow >= nextCodyRenewal)
                {
                    try
                    {
                        codyLease = await codyWorkerBroker.RenewAsync(codyLease, cancellationToken);
                        nextCodyRenewal = DateTimeOffset.UtcNow.AddSeconds(configuration.CodyWorkerRenewSeconds);
                    }
                    catch (Exception exception) when (exception is IOException or InvalidOperationException or JsonException or Win32Exception)
                    {
                        await InterruptAsync(
                            session,
                            job,
                            claimed.Lease,
                            partialDestination,
                            $"Cody worker broker lease could not be renewed: {exception.Message}",
                            cancellationToken);
                        return;
                    }
                }
                if (command == WorkerControlCommand.Pause)
                {
                    pauseUntilIdle = true;
                    await session.PauseAsync();
                }
                else if (command == WorkerControlCommand.Resume)
                {
                    pauseUntilIdle = false;
                    await session.ResumeAsync();
                }
                if (pauseUntilIdle && availability.CanClaim)
                {
                    pauseUntilIdle = false;
                    await session.ResumeAsync();
                }

                var current = session.Progress;
                var reportedAvailability = availability.Availability == WorkerAvailability.HumanActive ||
                                           availability.ActivityState == WorkerActivityState.IdleCooldown
                    ? WorkerAvailability.Draining
                    : availability.Availability;
                var reason = reportedAvailability == WorkerAvailability.Draining
                    ? "Human activity resumed; finishing the current video unless the user pauses or requeues it."
                    : availability.Reason;
                var status = new WorkerStatusSnapshot(reportedAvailability, reason, job.DisplayName, current.Fraction, current.EtaSeconds, session.IsPaused);
                runtimeStatus.Update(status);
                if (reportedAvailability == WorkerAvailability.Draining && !humanReturnNotified)
                {
                    humanReturnNotified = true;
                    await notifier.HumanReturnedAsync(status, cancellationToken);
                }
                else if (availability.CanClaim)
                {
                    humanReturnNotified = false;
                }

                try
                {
                    await coordinator.HeartbeatAsync(CreateHeartbeat(
                        new AvailabilitySnapshot(reportedAvailability, reason), job.Id, claimed.Lease), cancellationToken);
                    var accepted = await coordinator.ProgressAsync(job.Id, CreateProgress(
                        claimed.Lease, current.Fraction, current.EtaSeconds, session.IsPaused), cancellationToken);
                    if (!accepted)
                    {
                        await session.StopAsync(cancellationToken);
                        DeletePartial(partialDestination);
                        log($"Discarded stale work for '{job.DisplayName}' after the coordinator rejected its fencing token.");
                        return;
                    }
                    lastCoordinatorContact = DateTimeOffset.UtcNow;
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
                {
                    log($"Could not renew '{job.DisplayName}': {exception.Message}");
                    if (DateTimeOffset.UtcNow - lastCoordinatorContact > TimeSpan.FromSeconds(configuration.CoordinatorLossStopSeconds))
                    {
                        await session.StopAsync(cancellationToken);
                        DeletePartial(partialDestination);
                        log($"Stopped '{job.DisplayName}' before its lease could become unsafe. The coordinator will requeue it on expiry.");
                        return;
                    }
                }

                await DelaySafelyAsync(TimeSpan.FromSeconds(configuration.PollIntervalSeconds), cancellationToken);
            }

            var result = await session.Completion;
            if (result.ExitCode != 0)
            {
                var brokerAvailability = codyLease is null
                    ? null
                    : availabilityProbe.Check(jobRunning: true, ownedGameLeaseId: codyLease.LeaseId);
                if (brokerAvailability?.RequiresImmediateYield == true)
                {
                    await InterruptAsync(
                        session,
                        job,
                        claimed.Lease,
                        partialDestination,
                        $"The fenced broker command ended after development work took priority: {brokerAvailability.Reason}",
                        cancellationToken);
                    return;
                }
                DeletePartial(partialDestination);
                await coordinator.FailAsync(job.Id, new FailureReport
                {
                    WorkerId = configuration.WorkerId,
                    Lease = claimed.Lease,
                    Error = result.ErrorSummary ?? "HandBrakeCLI failed.",
                    ExitCode = result.ExitCode
                }, cancellationToken);
                return;
            }
            if (!File.Exists(partialDestination) || new FileInfo(partialDestination).Length == 0)
            {
                DeletePartial(partialDestination);
                await coordinator.FailAsync(job.Id, new FailureReport
                {
                    WorkerId = configuration.WorkerId,
                    Lease = claimed.Lease,
                    Error = "HandBrakeCLI exited successfully but produced no nonempty output."
                }, cancellationToken);
                return;
            }

            if (codyLease is not null && codyWorkerBroker is not null)
            {
                try
                {
                    codyLease = await codyWorkerBroker.RenewAsync(codyLease, cancellationToken);
                    var brokerAvailability = availabilityProbe.Check(jobRunning: true, ownedGameLeaseId: codyLease.LeaseId);
                    if (brokerAvailability.RequiresImmediateYield)
                    {
                        await InterruptAsync(
                            session,
                            job,
                            claimed.Lease,
                            partialDestination,
                            $"Publication yielded to development work: {brokerAvailability.Reason}",
                            cancellationToken);
                        return;
                    }
                }
                catch (Exception exception) when (exception is IOException or InvalidOperationException or JsonException or Win32Exception)
                {
                    await InterruptAsync(
                        session,
                        job,
                        claimed.Lease,
                        partialDestination,
                        $"Publication was canceled because the Cody worker fence could not be renewed: {exception.Message}",
                        cancellationToken);
                    return;
                }
            }

            // This accepted progress update is a final fencing check immediately before publication.
            if (!await coordinator.ProgressAsync(job.Id, CreateProgress(claimed.Lease, 1, 0, false), cancellationToken))
            {
                DeletePartial(partialDestination);
                log($"Did not publish '{job.DisplayName}' because its lease was stale.");
                return;
            }
            if (File.Exists(destination))
            {
                DeletePartial(partialDestination);
                await ReportNonRetryableFailure(job, claimed.Lease, $"Destination appeared before publication; BackBurner did not overwrite it: {job.DestinationPath}", cancellationToken);
                return;
            }
            File.Move(partialDestination, destination, overwrite: false);
            var outputBytes = new FileInfo(destination).Length;
            if (!await coordinator.CompleteAsync(job.Id, new CompletionReport
            {
                WorkerId = configuration.WorkerId,
                Lease = claimed.Lease,
                OutputBytes = outputBytes
            }, cancellationToken))
            {
                log($"WARNING: '{job.DisplayName}' was published but completion acknowledgement was rejected. Reconciliation is required.");
            }
            else
            {
                await notifier.InformationAsync("BackBurner finished", $"{job.DisplayName} was published.", cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (session is not null) await session.StopAsync(CancellationToken.None);
            if (partialDestination is not null) DeletePartial(partialDestination);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await coordinator.InterruptAsync(job.Id, new InterruptionReport
                {
                    WorkerId = configuration.WorkerId,
                    Lease = claimed.Lease,
                    Reason = "Worker service shut down."
                }, timeout.Token);
            }
            catch (Exception) { /* Lease expiration is the fallback. */ }
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
        {
            if (session is not null) await session.StopAsync(cancellationToken);
            if (partialDestination is not null) DeletePartial(partialDestination);
            await ReportNonRetryableFailure(job, claimed.Lease, exception.Message, cancellationToken);
        }
        finally
        {
            if (session is not null) await session.DisposeAsync();
        }
    }

    private async Task<bool> CompletePreflightAsync(JobLease claimed, CancellationToken cancellationToken)
    {
        if (configuration.Mode != WorkerMode.PersonalDesktop || configuration.PreflightSeconds == 0)
        {
            return true;
        }

        await notifier.InformationAsync(
            "BackBurner is about to start",
            $"{claimed.Job.DisplayName} will begin in {configuration.PreflightSeconds} seconds. Use the tray menu to pause new jobs.",
            cancellationToken);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(configuration.PreflightSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await DelaySafelyAsync(TimeSpan.FromSeconds(Math.Min(2, configuration.PollIntervalSeconds)), cancellationToken);
            var availability = availabilityProbe.Check();
            if (!availability.CanClaim)
            {
                await coordinator.InterruptAsync(claimed.Job.Id, new InterruptionReport
                {
                    WorkerId = configuration.WorkerId,
                    Lease = claimed.Lease,
                    Reason = $"Preflight was canceled because the host became unavailable: {availability.Reason}"
                }, cancellationToken);
                return false;
            }
            await coordinator.HeartbeatAsync(CreateHeartbeat(availability, claimed.Job.Id, claimed.Lease), cancellationToken);
        }
        return true;
    }

    private async Task InterruptAsync(
        HandBrakeSession session,
        JobRecord job,
        LeaseProof lease,
        string partialDestination,
        string reason,
        CancellationToken cancellationToken)
    {
        await session.StopAsync(cancellationToken);
        DeletePartial(partialDestination);
        await coordinator.InterruptAsync(job.Id, new InterruptionReport
        {
            WorkerId = configuration.WorkerId,
            Lease = lease,
            Reason = reason
        }, cancellationToken);
    }

    private Task<bool> ReportNonRetryableFailure(JobRecord job, LeaseProof lease, string error, CancellationToken cancellationToken)
    {
        return coordinator.FailAsync(job.Id, new FailureReport
        {
            WorkerId = configuration.WorkerId,
            Lease = lease,
            Error = error,
            Retryable = false,
            ConsumesAttempt = false
        }, cancellationToken);
    }

    private WorkerHeartbeat CreateHeartbeat(
        AvailabilitySnapshot availability,
        Guid? activeJobId = null,
        LeaseProof? lease = null)
    {
        return new WorkerHeartbeat
        {
            WorkerId = configuration.WorkerId,
            DisplayName = configuration.DisplayName,
            Mode = configuration.Mode,
            Availability = availability.Availability,
            ActivityState = availability.ActivityState,
            AvailabilityReason = availability.Reason,
            ReadyAt = availability.ReadyAt,
            Capabilities = configuration.Capabilities,
            Profile = profile,
            ActiveJobId = activeJobId,
            Lease = lease
        };
    }

    private ProgressReport CreateProgress(LeaseProof lease, decimal fraction, int? etaSeconds, bool paused)
    {
        return new ProgressReport
        {
            WorkerId = configuration.WorkerId,
            Lease = lease,
            Progress = fraction,
            EtaSeconds = etaSeconds,
            IsPaused = paused
        };
    }

    private static void DeletePartial(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static async Task DelaySafelyAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try { await Task.Delay(delay, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task ReleaseCodyLeaseAsync(CodyWorkerBroker broker, CodyWorkerLease lease)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await broker.ReleaseAsync(lease, timeout.Token);
            log($"Released Cody worker broker generation {lease.Generation}.");
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or JsonException or Win32Exception or OperationCanceledException)
        {
            log($"CRITICAL: Cody worker broker lease {lease.LeaseId} could not be released cleanly: {exception.Message}");
        }
    }

    public void Dispose() => coordinator.Dispose();
}

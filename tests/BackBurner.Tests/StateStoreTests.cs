using BackBurner.Contracts;
using BackBurner.Server;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

namespace BackBurner.Tests;

public sealed class StateStoreTests : IDisposable
{
    private readonly string temporaryRoot = Path.Combine(Path.GetTempPath(), "backburner-state-tests", Guid.NewGuid().ToString("N"));
    private readonly StateStore store;

    public StateStoreTests()
    {
        Directory.CreateDirectory(temporaryRoot);
        store = new StateStore(Options.Create(new CoordinatorOptions
        {
            DataFile = "state.json",
            LeaseSeconds = 30,
            OfflineAfterSeconds = 30,
            RetryBaseDelaySeconds = 0
        }), new TestEnvironment(temporaryRoot));
    }

    [Fact]
    public async Task Three_encoder_failures_end_in_terminal_failure()
    {
        var job = await EnqueueX265();
        await RegisterWorker("worker-a", "encode:x265");

        for (var failure = 1; failure <= 3; failure++)
        {
            var claim = await store.ClaimAsync("worker-a", CancellationToken.None);
            Assert.NotNull(claim);
            Assert.True(await store.FailAsync(job.Id, new FailureReport
            {
                WorkerId = "worker-a",
                Lease = claim.Lease,
                Error = $"failure {failure}",
                ExitCode = 1
            }, CancellationToken.None));
        }

        var finished = (await store.SnapshotAsync(CancellationToken.None)).Jobs.Single(item => item.Id == job.Id);
        Assert.Equal(JobStatus.Failed, finished.Status);
        Assert.Equal(3, finished.FailureCount);
        Assert.Equal(3, finished.Attempts.Count);
        Assert.All(finished.Attempts, attempt => Assert.Equal(AttemptOutcome.EncoderFailed, attempt.Outcome));
    }

    [Fact]
    public async Task Human_interruption_requeues_without_consuming_failure_budget_and_fences_old_worker()
    {
        var job = await EnqueueX265();
        await RegisterWorker("worker-a", "encode:x265");
        var first = await store.ClaimAsync("worker-a", CancellationToken.None);
        Assert.NotNull(first);

        Assert.True(await store.InterruptAsync(job.Id, new InterruptionReport
        {
            WorkerId = "worker-a",
            Lease = first.Lease,
            Reason = "Human returned."
        }, CancellationToken.None));
        Assert.False(await store.ProgressAsync(job.Id, new ProgressReport
        {
            WorkerId = "worker-a",
            Lease = first.Lease,
            Progress = .8m
        }, CancellationToken.None));

        var second = await store.ClaimAsync("worker-a", CancellationToken.None);
        Assert.NotNull(second);
        Assert.True(second.Lease.Generation > first.Lease.Generation);
        var current = (await store.SnapshotAsync(CancellationToken.None)).Jobs.Single(item => item.Id == job.Id);
        Assert.Equal(0, current.FailureCount);
        Assert.Equal(1, current.InterruptionCount);
    }

    [Fact]
    public async Task Scheduler_only_leases_jobs_to_workers_with_every_required_capability()
    {
        await EnqueueX265();
        await RegisterWorker("x264-only", "encode:x264");

        Assert.Null(await store.ClaimAsync("x264-only", CancellationToken.None));
    }

    [Fact]
    public async Task Worker_heartbeat_persists_typed_role_and_readiness_time()
    {
        var readyAt = DateTimeOffset.UtcNow.AddMinutes(12);
        await store.HeartbeatAsync(new WorkerHeartbeat
        {
            WorkerId = "cody-runner",
            DisplayName = "Cody runner",
            Mode = WorkerMode.SharedGameWorker,
            Availability = WorkerAvailability.HumanActive,
            ActivityState = WorkerActivityState.IdleCooldown,
            AvailabilityReason = "Waiting for a stable idle window.",
            ReadyAt = readyAt,
            Capabilities = ["handbrake", "encode:x265"]
        }, CancellationToken.None);

        var worker = (await store.SnapshotAsync(CancellationToken.None)).Workers.Single();

        Assert.Equal(WorkerMode.SharedGameWorker, worker.Mode);
        Assert.Equal(WorkerAvailability.HumanActive, worker.Availability);
        Assert.Equal(WorkerActivityState.IdleCooldown, worker.ActivityState);
        Assert.Equal(readyAt, worker.ReadyAt);
    }

    [Fact]
    public async Task Nonretryable_validation_error_does_not_consume_encoding_attempt()
    {
        var job = await EnqueueX265();
        await RegisterWorker("worker-a", "encode:x265");
        var claim = await store.ClaimAsync("worker-a", CancellationToken.None);
        Assert.NotNull(claim);

        Assert.True(await store.FailAsync(job.Id, new FailureReport
        {
            WorkerId = "worker-a",
            Lease = claim.Lease,
            Error = "Destination exists.",
            Retryable = false,
            ConsumesAttempt = false
        }, CancellationToken.None));

        var failed = (await store.SnapshotAsync(CancellationToken.None)).Jobs.Single(item => item.Id == job.Id);
        Assert.Equal(JobStatus.Failed, failed.Status);
        Assert.Equal(0, failed.FailureCount);
        Assert.Equal(AttemptOutcome.NonRetryableFailure, failed.Attempts.Single().Outcome);
    }

    [Fact]
    public async Task Batch_enqueues_independent_jobs_with_one_batch_identity_and_snapshot()
    {
        var batch = await store.EnqueueBatchAsync(new CreateBatchRequest
        {
            DisplayName = "Example season",
            SourceDirectory = "nas-media:/Example/Season 01",
            Settings = new HandBrakeSettings { VideoEncoder = "x265", Quality = 21 },
            MaxAttempts = 3,
            Items =
            [
                new BatchItemRequest
                {
                    DisplayName = "Episode 01",
                    SourcePath = "nas-media:/Example/Season 01/Episode 01.mkv",
                    DestinationPath = "plex-series:/Example (2026)/Season 01/Episode 01.mkv"
                },
                new BatchItemRequest
                {
                    DisplayName = "Episode 02",
                    SourcePath = "nas-media:/Example/Season 01/Episode 02.mkv",
                    DestinationPath = "plex-series:/Example (2026)/Season 01/Episode 02.mkv"
                }
            ]
        }, CancellationToken.None);

        var snapshot = await store.SnapshotAsync(CancellationToken.None);

        Assert.Equal(batch.Id, snapshot.Batches.Single().Id);
        Assert.Equal(2, batch.JobIds.Length);
        Assert.All(snapshot.Jobs, job => Assert.Equal(batch.Id, job.BatchId));
        Assert.All(snapshot.Jobs, job => Assert.Equal(21, job.Settings.Quality));
        Assert.Contains(snapshot.Events, item => item.Type == "batch.queued");
    }

    [Fact]
    public async Task Batch_rejects_a_source_outside_its_scanned_directory_without_queueing_anything()
    {
        var request = new CreateBatchRequest
        {
            DisplayName = "Unsafe batch",
            SourceDirectory = "nas-media:/Example/Season 01",
            Settings = new HandBrakeSettings { VideoEncoder = "x265" },
            Items =
            [
                new BatchItemRequest
                {
                    DisplayName = "Outside",
                    SourcePath = "nas-media:/Different Show/Episode.mkv",
                    DestinationPath = "plex-series:/Example/Episode.mkv"
                }
            ]
        };

        await Assert.ThrowsAsync<ArgumentException>(() => store.EnqueueBatchAsync(request, CancellationToken.None));

        var snapshot = await store.SnapshotAsync(CancellationToken.None);
        Assert.Empty(snapshot.Batches);
        Assert.Empty(snapshot.Jobs);
    }

    [Theory]
    [InlineData("--output=stolen.mkv")]
    [InlineData("--input=other.mkv")]
    [InlineData("-ostolen.mkv")]
    [InlineData("--preset-import-file=settings.json")]
    public async Task Extra_arguments_cannot_override_coordinator_owned_paths_or_imports(string argument)
    {
        var request = new CreateJobRequest
        {
            DisplayName = "Unsafe override",
            SourcePath = "incoming:/source.mkv",
            DestinationPath = "plex-movies:/Test (2026)/Test (2026).mkv",
            Settings = new HandBrakeSettings { VideoEncoder = "x265", ExtraArguments = [argument] }
        };

        await Assert.ThrowsAsync<ArgumentException>(() => store.EnqueueAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task Passwordless_identity_is_snapshotted_on_jobs_and_can_own_workers()
    {
        var identity = await store.CreateIdentityAsync(new CreateIdentityRequest("Cody"), CancellationToken.None);
        await RegisterWorker("worker-a", "encode:x265");

        Assert.True(await store.SetWorkerOwnerAsync(
            "worker-a",
            new SetWorkerOwnerRequest(identity.Id),
            CancellationToken.None));
        var job = await store.EnqueueAsync(new CreateJobRequest
        {
            DisplayName = "Attributed video",
            SourcePath = "incoming:/source.mkv",
            DestinationPath = "plex-movies:/Test (2026)/Attributed.mkv",
            Settings = new HandBrakeSettings { VideoEncoder = "x265" },
            IdentityId = identity.Id
        }, CancellationToken.None);

        var snapshot = await store.SnapshotAsync(CancellationToken.None);

        Assert.Equal("Cody", job.SubmittedBy);
        Assert.Equal(identity.Id, job.IdentityId);
        Assert.Equal(identity.Id, snapshot.Workers.Single().OwnerIdentityId);
        Assert.Equal("Cody", snapshot.Identities.Single().DisplayName);
        Assert.Contains(snapshot.Events, item => item.Type == "job.queued" && item.IdentityId == identity.Id);
    }

    [Fact]
    public async Task Worker_history_records_state_transitions_instead_of_every_heartbeat()
    {
        var humanHeartbeat = new WorkerHeartbeat
        {
            WorkerId = "worker-a",
            DisplayName = "Worker A",
            Availability = WorkerAvailability.HumanActive,
            ActivityState = WorkerActivityState.HumanActive,
            BlockingCategory = WorkerBlockingCategory.HumanActivity,
            AvailabilityReason = "Human input is active.",
            Capabilities = ["handbrake", "encode:x265"]
        };
        await store.HeartbeatAsync(humanHeartbeat, CancellationToken.None);
        await store.HeartbeatAsync(humanHeartbeat, CancellationToken.None);
        await store.HeartbeatAsync(humanHeartbeat with
        {
            Availability = WorkerAvailability.Available,
            ActivityState = WorkerActivityState.None,
            BlockingCategory = WorkerBlockingCategory.None,
            AvailabilityReason = "No work to do."
        }, CancellationToken.None);

        var snapshot = await store.SnapshotAsync(CancellationToken.None);
        var history = snapshot.WorkerActivities.OrderBy(item => item.StartedAt).ToArray();

        Assert.Equal(2, history.Length);
        Assert.Equal(WorkerActivityKind.HumanActivity, history[0].Kind);
        Assert.NotNull(history[0].EndedAt);
        Assert.Equal(WorkerActivityKind.AvailableNoWork, history[1].Kind);
        Assert.Null(history[1].EndedAt);
    }

    [Fact]
    public async Task Successful_completion_records_output_size_and_cpu_activity()
    {
        var job = await EnqueueX265();
        await RegisterWorker("worker-a", "encode:x265");
        var claim = await store.ClaimAsync("worker-a", CancellationToken.None);
        Assert.NotNull(claim);

        Assert.True(await store.CompleteAsync(job.Id, new CompletionReport
        {
            WorkerId = "worker-a",
            Lease = claim.Lease,
            OutputBytes = 123_456
        }, CancellationToken.None));

        var snapshot = await store.SnapshotAsync(CancellationToken.None);
        Assert.Equal(123_456, snapshot.Jobs.Single().OutputBytes);
        Assert.Contains(snapshot.WorkerActivities, item => item.Kind == WorkerActivityKind.EncodingCpu && item.JobId == job.Id);
    }

    [Fact]
    public async Task Integration_control_token_reads_and_cancels_only_its_job_without_being_persisted()
    {
        var created = await EnqueueIntegrationX265();

        Assert.Matches("^[A-Za-z0-9_-]{43}$", created.ControlToken);
        Assert.Null(await store.GetIntegrationJobAsync(created.JobId, "wrong-token", CancellationToken.None));
        var queued = await store.GetIntegrationJobAsync(created.JobId, created.ControlToken, CancellationToken.None);
        Assert.NotNull(queued);
        Assert.Equal(JobStatus.Queued, queued.Status);
        Assert.False(queued.IsTerminal);

        var persistedJson = await File.ReadAllTextAsync(Path.Combine(temporaryRoot, "state.json"));
        Assert.DoesNotContain(created.ControlToken, persistedJson, StringComparison.Ordinal);

        var rejected = await store.CancelIntegrationJobAsync(created.JobId, "wrong-token", CancellationToken.None);
        Assert.Equal(IntegrationCancelOutcome.NotFound, rejected.Outcome);

        var canceled = await store.CancelIntegrationJobAsync(created.JobId, created.ControlToken, CancellationToken.None);
        Assert.Equal(IntegrationCancelOutcome.Canceled, canceled.Outcome);
        Assert.Equal(JobStatus.Canceled, canceled.Job?.Status);
        Assert.True(canceled.Job?.IsTerminal);
        Assert.Equal(0, canceled.Job?.FailureCount);

        var repeated = await store.CancelIntegrationJobAsync(created.JobId, created.ControlToken, CancellationToken.None);
        Assert.Equal(IntegrationCancelOutcome.AlreadyCanceled, repeated.Outcome);
        var snapshot = await store.SnapshotAsync(CancellationToken.None);
        Assert.Contains(snapshot.Events, item => item.Type == "job.canceled" && item.JobId == created.JobId);

        var reloadedStore = new StateStore(Options.Create(new CoordinatorOptions
        {
            DataFile = "state.json",
            LeaseSeconds = 30,
            OfflineAfterSeconds = 30,
            RetryBaseDelaySeconds = 0
        }), new TestEnvironment(temporaryRoot));
        var reloaded = await reloadedStore.GetIntegrationJobAsync(created.JobId, created.ControlToken, CancellationToken.None);
        Assert.Equal(JobStatus.Canceled, reloaded?.Status);
    }

    [Fact]
    public async Task Canceling_active_integration_work_interrupts_attempt_and_fences_worker_without_failure()
    {
        var created = await EnqueueIntegrationX265();
        await RegisterWorker("worker-a", "encode:x265", BackBurnerCapabilities.PublicationFenceV1);
        var claim = await store.ClaimAsync("worker-a", CancellationToken.None);
        Assert.NotNull(claim);

        var canceled = await store.CancelIntegrationJobAsync(created.JobId, created.ControlToken, CancellationToken.None);
        Assert.Equal(IntegrationCancelOutcome.Canceled, canceled.Outcome);
        Assert.False(await store.ProgressAsync(created.JobId, new ProgressReport
        {
            WorkerId = "worker-a",
            Lease = claim.Lease,
            Progress = .5m
        }, CancellationToken.None));
        await store.HeartbeatAsync(new WorkerHeartbeat
        {
            WorkerId = "worker-a",
            DisplayName = "worker-a",
            Availability = WorkerAvailability.Available,
            Capabilities = ["handbrake", "encode:x265", BackBurnerCapabilities.PublicationFenceV1],
            ActiveJobId = created.JobId,
            Lease = claim.Lease
        }, CancellationToken.None);

        var snapshot = await store.SnapshotAsync(CancellationToken.None);
        var job = snapshot.Jobs.Single(item => item.Id == created.JobId);
        Assert.Equal(JobStatus.Canceled, job.Status);
        Assert.Equal(0, job.FailureCount);
        Assert.Equal(1, job.InterruptionCount);
        Assert.Equal(AttemptOutcome.Interrupted, job.Attempts.Single().Outcome);
        Assert.Null(snapshot.Workers.Single(item => item.WorkerId == "worker-a").ActiveJobId);
    }

    [Fact]
    public async Task Integration_cannot_cancel_a_job_after_successful_publication()
    {
        var created = await EnqueueIntegrationX265();
        await RegisterWorker("worker-a", "encode:x265", BackBurnerCapabilities.PublicationFenceV1);
        var claim = await store.ClaimAsync("worker-a", CancellationToken.None);
        Assert.NotNull(claim);
        Assert.True(await store.AuthorizePublicationAsync(created.JobId, new PublicationAuthorizationRequest
        {
            WorkerId = "worker-a",
            Lease = claim.Lease
        }, CancellationToken.None));
        Assert.True(await store.CompleteAsync(created.JobId, new CompletionReport
        {
            WorkerId = "worker-a",
            Lease = claim.Lease,
            OutputBytes = 1_024
        }, CancellationToken.None));

        var result = await store.CancelIntegrationJobAsync(created.JobId, created.ControlToken, CancellationToken.None);
        Assert.Equal(IntegrationCancelOutcome.TerminalConflict, result.Outcome);
        Assert.Equal(JobStatus.Succeeded, result.Job?.Status);
    }

    [Fact]
    public async Task Fenced_publication_authorization_wins_deterministically_over_late_cancellation()
    {
        var created = await EnqueueIntegrationX265();
        await RegisterWorker("worker-a", "encode:x265", BackBurnerCapabilities.PublicationFenceV1);
        var claim = await store.ClaimAsync("worker-a", CancellationToken.None);
        Assert.NotNull(claim);

        Assert.True(await store.AuthorizePublicationAsync(created.JobId, new PublicationAuthorizationRequest
        {
            WorkerId = "worker-a",
            Lease = claim.Lease
        }, CancellationToken.None));
        var result = await store.CancelIntegrationJobAsync(created.JobId, created.ControlToken, CancellationToken.None);

        Assert.Equal(IntegrationCancelOutcome.TerminalConflict, result.Outcome);
        Assert.False(result.Job?.CancellationAllowed);
        Assert.True(await store.CompleteAsync(created.JobId, new CompletionReport
        {
            WorkerId = "worker-a",
            Lease = claim.Lease,
            OutputBytes = 2_048
        }, CancellationToken.None));
    }

    [Fact]
    public async Task Integration_completion_is_rejected_without_publication_authorization()
    {
        var created = await EnqueueIntegrationX265();
        await RegisterWorker("worker-a", "encode:x265", BackBurnerCapabilities.PublicationFenceV1);
        var claim = await store.ClaimAsync("worker-a", CancellationToken.None);
        Assert.NotNull(claim);

        Assert.False(await store.CompleteAsync(created.JobId, new CompletionReport
        {
            WorkerId = "worker-a",
            Lease = claim.Lease,
            OutputBytes = 2_048
        }, CancellationToken.None));
    }

    [Fact]
    public async Task Legacy_worker_without_publication_fence_capability_cannot_claim_integration_job()
    {
        await EnqueueIntegrationX265();
        await RegisterWorker("legacy-worker", "encode:x265");

        Assert.Null(await store.ClaimAsync("legacy-worker", CancellationToken.None));
    }

    [Fact]
    public async Task Integration_status_reports_bounded_queue_and_available_worker_capabilities()
    {
        var created = await EnqueueIntegrationX265();
        await RegisterWorker("worker-a", "encode:x265");

        var status = await store.IntegrationStatusAsync(100, CancellationToken.None);

        Assert.Equal("v1", status.ApiVersion);
        Assert.Equal(1, status.Jobs.Queued);
        Assert.Equal(1, status.Workers.Total);
        Assert.Equal(1, status.Workers.Available);
        Assert.Equal(1, status.AvailableCapabilities["encode:x265"]);
        Assert.Equal(created.JobId, status.Queue.Single().JobId);
        Assert.Null(status.WorkerDetails.Single().ActiveJob);
    }

    private Task<JobRecord> EnqueueX265()
    {
        return store.EnqueueAsync(new CreateJobRequest
        {
            DisplayName = "Test video",
            SourcePath = "incoming:/source.mkv",
            DestinationPath = "plex-movies:/Test (2026)/Test (2026).mkv",
            Settings = new HandBrakeSettings { VideoEncoder = "x265" },
            MaxAttempts = 3
        }, CancellationToken.None);
    }

    private Task<IntegrationJobCreatedResponse> EnqueueIntegrationX265()
    {
        return store.EnqueueIntegrationAsync(new CreateIntegrationJobRequest
        {
            DisplayName = "Integration test video",
            SourcePath = "incoming:/source.mkv",
            DestinationPath = "plex-movies:/Test (2026)/Integration.mkv",
            Settings = new HandBrakeSettings { VideoEncoder = "x265" },
            MaxAttempts = 3,
            ClientName = "test-client"
        }, CancellationToken.None);
    }

    private Task RegisterWorker(string workerId, params string[] extraCapabilities)
    {
        return store.HeartbeatAsync(new WorkerHeartbeat
        {
            WorkerId = workerId,
            DisplayName = workerId,
            Availability = WorkerAvailability.Available,
            Capabilities = ["handbrake", .. extraCapabilities]
        }, CancellationToken.None);
    }

    public void Dispose()
    {
        if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true);
    }

    private sealed class TestEnvironment(string contentRoot) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "BackBurner.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = contentRoot;
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

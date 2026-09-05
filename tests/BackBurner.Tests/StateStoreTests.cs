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

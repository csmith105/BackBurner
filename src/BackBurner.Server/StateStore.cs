using System.Text.Json;
using System.Text.Json.Serialization;
using BackBurner.Contracts;
using Microsoft.Extensions.Options;

namespace BackBurner.Server;

public sealed class StateStore
{
    private const int MaximumEvents = 1_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true
    };

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CoordinatorOptions options;
    private readonly string dataFile;
    private CoordinatorState state;

    public StateStore(IOptions<CoordinatorOptions> options, IWebHostEnvironment environment)
    {
        this.options = options.Value;
        dataFile = Path.GetFullPath(this.options.DataFile, environment.ContentRootPath);
        state = Load();
    }

    public async Task<DashboardSnapshot> SnapshotAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            MarkOfflineWorkers();
            return Clone(new DashboardSnapshot(
                state.Jobs.OrderByDescending(job => job.CreatedAt).ToArray(),
                state.Batches.OrderByDescending(batch => batch.CreatedAt).ToArray(),
                state.Presets.OrderBy(preset => preset.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
                state.Workers.OrderBy(worker => worker.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray(),
                state.Events.OrderByDescending(item => item.At).Take(200).ToArray()));
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<PresetRecord> UpsertPresetAsync(CreatePresetRequest request, CancellationToken cancellationToken)
    {
        ValidatePreset(request);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var existing = state.Presets.FirstOrDefault(item =>
                string.Equals(item.Name, request.Name.Trim(), StringComparison.OrdinalIgnoreCase));
            PresetRecord saved;
            if (existing is null)
            {
                saved = new PresetRecord
                {
                    Name = request.Name.Trim(),
                    Description = request.Description.Trim(),
                    Settings = request.Settings,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                state.Presets.Add(saved);
            }
            else
            {
                saved = existing with
                {
                    Description = request.Description.Trim(),
                    Settings = request.Settings,
                    UpdatedAt = now
                };
                state.Presets[state.Presets.IndexOf(existing)] = saved;
            }

            AddEvent("preset.saved", $"Preset '{saved.Name}' was saved.");
            await PersistAsync(cancellationToken);
            return Clone(saved);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> DeletePresetAsync(Guid presetId, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var preset = state.Presets.FirstOrDefault(item => item.Id == presetId);
            if (preset is null)
            {
                return false;
            }

            state.Presets.Remove(preset);
            AddEvent("preset.deleted", $"Preset '{preset.Name}' was deleted. Existing jobs retain their snapshots.");
            await PersistAsync(cancellationToken);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<JobRecord> EnqueueAsync(CreateJobRequest request, CancellationToken cancellationToken)
    {
        ValidateJob(request);
        var requiredCapabilities = RequiredCapabilities(request.Settings);

        await gate.WaitAsync(cancellationToken);
        try
        {
            var job = new JobRecord
            {
                DisplayName = request.DisplayName.Trim(),
                SourcePath = request.SourcePath.Trim(),
                DestinationPath = request.DestinationPath.Trim(),
                PresetName = string.IsNullOrWhiteSpace(request.PresetName) ? null : request.PresetName.Trim(),
                Settings = request.Settings,
                RequiredCapabilities = requiredCapabilities,
                MaxAttempts = request.MaxAttempts,
                SubmittedBy = request.SubmittedBy.Trim()
            };
            state.Jobs.Add(job);
            AddEvent("job.queued", $"Queued '{job.DisplayName}' with a {job.MaxAttempts}-attempt limit.", job.Id);
            await PersistAsync(cancellationToken);
            return Clone(job);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<BatchRecord> EnqueueBatchAsync(CreateBatchRequest request, CancellationToken cancellationToken)
    {
        ValidateBatch(request);
        var requiredCapabilities = RequiredCapabilities(request.Settings);

        await gate.WaitAsync(cancellationToken);
        try
        {
            var batchId = Guid.NewGuid();
            var jobs = request.Items.Select(item => new JobRecord
            {
                DisplayName = item.DisplayName.Trim(),
                SourcePath = item.SourcePath.Trim(),
                DestinationPath = item.DestinationPath.Trim(),
                PresetName = string.IsNullOrWhiteSpace(request.PresetName) ? null : request.PresetName.Trim(),
                Settings = Clone(request.Settings),
                RequiredCapabilities = requiredCapabilities,
                BatchId = batchId,
                MaxAttempts = request.MaxAttempts,
                SubmittedBy = request.SubmittedBy.Trim()
            }).ToArray();
            var batch = new BatchRecord
            {
                Id = batchId,
                DisplayName = request.DisplayName.Trim(),
                SourceDirectory = request.SourceDirectory.Trim(),
                PresetName = string.IsNullOrWhiteSpace(request.PresetName) ? null : request.PresetName.Trim(),
                SubmittedBy = request.SubmittedBy.Trim(),
                JobIds = jobs.Select(job => job.Id).ToArray()
            };

            state.Jobs.AddRange(jobs);
            state.Batches.Add(batch);
            AddEvent("batch.queued", $"Queued batch '{batch.DisplayName}' with {jobs.Length} independently schedulable files.");
            await PersistAsync(cancellationToken);
            return Clone(batch);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task HeartbeatAsync(WorkerHeartbeat heartbeat, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(heartbeat.WorkerId))
        {
            throw new ArgumentException("Worker ID is required.");
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var worker = state.Workers.FirstOrDefault(item => item.WorkerId == heartbeat.WorkerId);
            if (worker is null)
            {
                worker = new WorkerRecord
                {
                    WorkerId = heartbeat.WorkerId,
                    DisplayName = heartbeat.DisplayName,
                    Availability = heartbeat.Availability
                };
                state.Workers.Add(worker);
                AddEvent("worker.registered", $"Worker '{heartbeat.DisplayName}' registered.", workerId: heartbeat.WorkerId);
            }

            worker.DisplayName = heartbeat.DisplayName;
            worker.Availability = heartbeat.Availability;
            worker.AvailabilityReason = heartbeat.AvailabilityReason;
            worker.Capabilities = heartbeat.Capabilities.Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray();
            worker.Profile = heartbeat.Profile;
            worker.LastSeenAt = now;
            worker.ActiveJobId = heartbeat.ActiveJobId;

            if (heartbeat.ActiveJobId is not null && heartbeat.Lease is not null)
            {
                var job = state.Jobs.FirstOrDefault(item => item.Id == heartbeat.ActiveJobId);
                if (job is not null && HasLease(job, heartbeat.WorkerId, heartbeat.Lease))
                {
                    job.LeaseExpiresAt = now.AddSeconds(options.LeaseSeconds);
                    job.UpdatedAt = now;
                }
            }

            await PersistAsync(cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<JobLease?> ClaimAsync(string workerId, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var worker = state.Workers.FirstOrDefault(item => item.WorkerId == workerId);
            if (worker is null || worker.Availability != WorkerAvailability.Available)
            {
                return null;
            }

            var capabilities = worker.Capabilities.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var job = state.Jobs
                .Where(item => item.Status == JobStatus.Queued)
                .Where(item => item.NextEligibleAt is null || item.NextEligibleAt <= now)
                .Where(item => item.RequiredCapabilities.All(capabilities.Contains))
                .OrderBy(item => item.CreatedAt)
                .FirstOrDefault();
            if (job is null)
            {
                return null;
            }

            var lease = new LeaseProof(Guid.NewGuid(), checked(job.FencingGeneration + 1));
            var expiresAt = now.AddSeconds(options.LeaseSeconds);
            job.Status = JobStatus.Leased;
            job.AssignedWorkerId = workerId;
            job.LeaseId = lease.LeaseId;
            job.FencingGeneration = lease.Generation;
            job.LeaseExpiresAt = expiresAt;
            job.NextEligibleAt = null;
            job.Progress = 0;
            job.EtaSeconds = null;
            job.UpdatedAt = now;
            job.Attempts.Add(new JobAttempt
            {
                WorkerId = workerId,
                LeaseId = lease.LeaseId,
                Generation = lease.Generation
            });
            worker.ActiveJobId = job.Id;
            AddEvent("job.leased", $"'{job.DisplayName}' leased to '{worker.DisplayName}' (generation {lease.Generation}).", job.Id, workerId);
            await PersistAsync(cancellationToken);
            return new JobLease(Clone(job), lease, expiresAt);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> ProgressAsync(Guid jobId, ProgressReport report, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var job = state.Jobs.FirstOrDefault(item => item.Id == jobId);
            if (job is null || !HasLease(job, report.WorkerId, report.Lease))
            {
                return false;
            }

            job.Status = report.IsPaused ? JobStatus.Paused : JobStatus.Running;
            job.Progress = Math.Clamp(report.Progress, 0, 1);
            job.EtaSeconds = report.EtaSeconds is null ? null : Math.Max(0, report.EtaSeconds.Value);
            job.LeaseExpiresAt = DateTimeOffset.UtcNow.AddSeconds(options.LeaseSeconds);
            job.UpdatedAt = DateTimeOffset.UtcNow;
            await PersistAsync(cancellationToken);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> CompleteAsync(Guid jobId, CompletionReport report, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var job = state.Jobs.FirstOrDefault(item => item.Id == jobId);
            if (job is null || !HasLease(job, report.WorkerId, report.Lease))
            {
                return false;
            }

            FinishCurrentAttempt(job, AttemptOutcome.Succeeded, $"Published {report.OutputBytes:N0} bytes.");
            job.Status = JobStatus.Succeeded;
            job.Progress = 1;
            job.EtaSeconds = 0;
            job.LastError = null;
            ClearLease(job);
            ClearWorkerJob(report.WorkerId, job.Id);
            AddEvent("job.succeeded", $"'{job.DisplayName}' completed and published.", job.Id, report.WorkerId);
            await PersistAsync(cancellationToken);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> FailAsync(Guid jobId, FailureReport report, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var job = state.Jobs.FirstOrDefault(item => item.Id == jobId);
            if (job is null || !HasLease(job, report.WorkerId, report.Lease))
            {
                return false;
            }

            if (report.ConsumesAttempt)
            {
                job.FailureCount++;
            }
            job.LastError = report.ExitCode is null ? report.Error : $"{report.Error} (exit {report.ExitCode})";
            FinishCurrentAttempt(job, report.ConsumesAttempt ? AttemptOutcome.EncoderFailed : AttemptOutcome.NonRetryableFailure, job.LastError);
            ClearLease(job);
            ClearWorkerJob(report.WorkerId, job.Id);
            if (!report.Retryable || job.FailureCount >= job.MaxAttempts)
            {
                job.Status = JobStatus.Failed;
                job.NextEligibleAt = null;
                var reason = report.Retryable
                    ? $"after {job.FailureCount} encoding attempts"
                    : "with a non-retryable error";
                AddEvent("job.failed", $"'{job.DisplayName}' failed {reason}: {job.LastError}", job.Id, report.WorkerId);
            }
            else
            {
                var exponent = Math.Min(Math.Max(job.FailureCount - 1, 0), 5);
                var delay = Math.Min(options.RetryBaseDelaySeconds * (1 << exponent), 15 * 60);
                job.Status = JobStatus.Queued;
                job.NextEligibleAt = DateTimeOffset.UtcNow.AddSeconds(delay);
                AddEvent("job.retry_scheduled", $"'{job.DisplayName}' encoding attempt {job.FailureCount} failed; retry {job.FailureCount + 1}/{job.MaxAttempts} is eligible in {delay} seconds.", job.Id, report.WorkerId);
            }

            await PersistAsync(cancellationToken);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> InterruptAsync(Guid jobId, InterruptionReport report, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var job = state.Jobs.FirstOrDefault(item => item.Id == jobId);
            if (job is null || !HasLease(job, report.WorkerId, report.Lease))
            {
                return false;
            }

            job.InterruptionCount++;
            FinishCurrentAttempt(job, AttemptOutcome.Interrupted, report.Reason);
            job.Status = JobStatus.Queued;
            job.NextEligibleAt = DateTimeOffset.UtcNow;
            job.Progress = 0;
            job.EtaSeconds = null;
            ClearLease(job);
            ClearWorkerJob(report.WorkerId, job.Id);
            AddEvent("job.interrupted", $"'{job.DisplayName}' was returned to the queue without consuming an encoding attempt: {report.Reason}", job.Id, report.WorkerId);
            await PersistAsync(cancellationToken);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> RetryFailedAsync(Guid jobId, bool resetFailureCount, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var job = state.Jobs.FirstOrDefault(item => item.Id == jobId && item.Status == JobStatus.Failed);
            if (job is null)
            {
                return false;
            }

            if (resetFailureCount)
            {
                job.FailureCount = 0;
            }
            else
            {
                job = job with { MaxAttempts = Math.Max(job.MaxAttempts, job.FailureCount + 1) };
                state.Jobs[state.Jobs.FindIndex(item => item.Id == jobId)] = job;
            }

            job.Status = JobStatus.Queued;
            job.NextEligibleAt = DateTimeOffset.UtcNow;
            job.UpdatedAt = DateTimeOffset.UtcNow;
            AddEvent("job.manual_retry", $"'{job.DisplayName}' was manually returned to the queue.", job.Id);
            await PersistAsync(cancellationToken);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<int> ExpireLeasesAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var expired = state.Jobs
                .Where(item => item.Status is JobStatus.Leased or JobStatus.Running or JobStatus.Paused)
                .Where(item => item.LeaseExpiresAt < now)
                .ToArray();

            foreach (var job in expired)
            {
                var oldWorker = job.AssignedWorkerId;
                job.InterruptionCount++;
                FinishCurrentAttempt(job, AttemptOutcome.Interrupted, "Worker lease expired.");
                job.Status = JobStatus.Queued;
                job.NextEligibleAt = now;
                job.Progress = 0;
                job.EtaSeconds = null;
                ClearLease(job);
                if (oldWorker is not null)
                {
                    ClearWorkerJob(oldWorker, job.Id);
                }
                AddEvent("job.lease_expired", $"'{job.DisplayName}' was requeued after its worker lease expired; no encoding failure was charged.", job.Id, oldWorker);
            }

            MarkOfflineWorkers();
            if (expired.Length > 0)
            {
                await PersistAsync(cancellationToken);
            }
            return expired.Length;
        }
        finally
        {
            gate.Release();
        }
    }

    private CoordinatorState Load()
    {
        if (!File.Exists(dataFile))
        {
            return SeedState();
        }

        try
        {
            var json = File.ReadAllText(dataFile);
            return JsonSerializer.Deserialize<CoordinatorState>(json, JsonOptions) ?? SeedState();
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            throw new InvalidOperationException($"Coordinator state at '{dataFile}' could not be loaded. Refusing to start rather than overwrite it.", exception);
        }
    }

    private static CoordinatorState SeedState()
    {
        return new CoordinatorState
        {
            Presets =
            [
                new PresetRecord
                {
                    Name = "H.265 MKV 1080p (starter)",
                    Description = "Starter only; Cameron should tune and save real household presets after lab encodes.",
                    Settings = new HandBrakeSettings
                    {
                        Container = "mkv",
                        VideoEncoder = "x265",
                        Quality = 22,
                        EncoderPreset = "medium",
                        MaxWidth = 1920,
                        MaxHeight = 1080
                    }
                }
            ]
        };
    }

    private async Task PersistAsync(CancellationToken cancellationToken)
    {
        if (state.Events.Count > MaximumEvents)
        {
            state.Events.RemoveRange(0, state.Events.Count - MaximumEvents);
        }

        var directory = Path.GetDirectoryName(dataFile) ?? throw new InvalidOperationException("State path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(dataFile)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, dataFile, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private void AddEvent(string type, string message, Guid? jobId = null, string? workerId = null)
    {
        state.Events.Add(new CoordinatorEvent
        {
            Type = type,
            Message = message,
            JobId = jobId,
            WorkerId = workerId
        });
    }

    private void MarkOfflineWorkers()
    {
        var cutoff = DateTimeOffset.UtcNow.AddSeconds(-options.OfflineAfterSeconds);
        foreach (var worker in state.Workers.Where(item => item.LastSeenAt < cutoff))
        {
            worker.Availability = WorkerAvailability.Offline;
            worker.AvailabilityReason = "Heartbeat overdue.";
        }
    }

    private static bool HasLease(JobRecord job, string workerId, LeaseProof proof)
    {
        return job.AssignedWorkerId == workerId &&
               job.LeaseId == proof.LeaseId &&
               job.FencingGeneration == proof.Generation &&
               job.Status is JobStatus.Leased or JobStatus.Running or JobStatus.Paused;
    }

    private static void FinishCurrentAttempt(JobRecord job, AttemptOutcome outcome, string? detail)
    {
        var attempt = job.Attempts.LastOrDefault(item => item.Outcome == AttemptOutcome.Running);
        if (attempt is null)
        {
            return;
        }
        attempt.Outcome = outcome;
        attempt.Detail = detail;
        attempt.FinishedAt = DateTimeOffset.UtcNow;
    }

    private static void ClearLease(JobRecord job)
    {
        job.AssignedWorkerId = null;
        job.LeaseId = null;
        job.LeaseExpiresAt = null;
        job.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private void ClearWorkerJob(string workerId, Guid jobId)
    {
        var worker = state.Workers.FirstOrDefault(item => item.WorkerId == workerId);
        if (worker?.ActiveJobId == jobId)
        {
            worker.ActiveJobId = null;
        }
    }

    private static void ValidatePreset(CreatePresetRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 100)
        {
            throw new ArgumentException("Preset name must contain 1-100 characters.");
        }
        ValidateSettings(request.Settings);
    }

    private static void ValidateJob(CreateJobRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Trim().Length > 200)
        {
            throw new ArgumentException("Job display name must contain 1-200 characters.");
        }
        ValidateLogicalPath(request.SourcePath, allowAnyRoot: true);
        ValidateLogicalPath(request.DestinationPath, allowAnyRoot: false);
        if (string.Equals(request.SourcePath.Trim(), request.DestinationPath.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Source and destination must differ.");
        }
        if (request.MaxAttempts is < 1 or > 10)
        {
            throw new ArgumentException("Max attempts must be between 1 and 10.");
        }
        ValidateSettings(request.Settings);
    }

    private static void ValidateBatch(CreateBatchRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Trim().Length > 200)
        {
            throw new ArgumentException("Batch display name must contain 1-200 characters.");
        }
        ValidateLogicalDirectory(request.SourceDirectory);
        if (request.Items is null || request.Items.Length is < 1 or > 5_000)
        {
            throw new ArgumentException("A batch must contain between 1 and 5,000 selected files.");
        }
        if (request.MaxAttempts is < 1 or > 10)
        {
            throw new ArgumentException("Max attempts must be between 1 and 10.");
        }
        ValidateSettings(request.Settings);

        var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in request.Items)
        {
            if (string.IsNullOrWhiteSpace(item.DisplayName) || item.DisplayName.Trim().Length > 200)
            {
                throw new ArgumentException("Every batch item display name must contain 1-200 characters.");
            }
            ValidateLogicalPath(item.SourcePath, allowAnyRoot: true);
            ValidateLogicalPath(item.DestinationPath, allowAnyRoot: false);
            if (!IsWithinLogicalDirectory(request.SourceDirectory, item.SourcePath))
            {
                throw new ArgumentException($"Batch source '{item.SourcePath}' is outside the scanned directory.");
            }
            if (!sources.Add(item.SourcePath.Trim()))
            {
                throw new ArgumentException($"Batch source '{item.SourcePath}' was selected more than once.");
            }
            if (!destinations.Add(item.DestinationPath.Trim()))
            {
                throw new ArgumentException($"Multiple batch items target '{item.DestinationPath}'.");
            }
            if (string.Equals(item.SourcePath.Trim(), item.DestinationPath.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Batch source and destination paths must differ.");
            }
        }
    }

    private static void ValidateLogicalDirectory(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Logical directory is required.");
        }
        var separator = value.IndexOf(":/", StringComparison.Ordinal);
        if (separator < 1 || separator > 50)
        {
            throw new ArgumentException($"'{value}' is not a logical directory such as incoming:/Season 01.");
        }
        var root = value[..separator];
        if (root.Any(character => !(char.IsAsciiLetterOrDigit(character) || character == '-')))
        {
            throw new ArgumentException($"Logical root '{root}' contains unsupported characters.");
        }
        var parts = value[(separator + 2)..].Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(part => part is "." or ".."))
        {
            throw new ArgumentException("Logical directories may not contain traversal segments.");
        }
    }

    private static bool IsWithinLogicalDirectory(string directory, string file)
    {
        var normalizedDirectory = directory.Trim().Replace('\\', '/').TrimEnd('/');
        var normalizedFile = file.Trim().Replace('\\', '/');
        return normalizedFile.StartsWith($"{normalizedDirectory}/", StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateLogicalPath(string value, bool allowAnyRoot)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Logical path is required.");
        }
        var separator = value.IndexOf(":/", StringComparison.Ordinal);
        if (separator < 1 || separator > 50)
        {
            throw new ArgumentException($"'{value}' is not a logical path such as incoming:/file.mkv.");
        }
        var root = value[..separator];
        if (root.Any(character => !(char.IsAsciiLetterOrDigit(character) || character == '-')))
        {
            throw new ArgumentException($"Logical root '{root}' contains unsupported characters.");
        }
        if (!allowAnyRoot && root is not ("plex-movies" or "plex-series"))
        {
            throw new ArgumentException("Destination root must be plex-movies or plex-series.");
        }
        var parts = value[(separator + 2)..].Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(part => part is "." or ".."))
        {
            throw new ArgumentException("Logical paths must name a file and may not contain traversal segments.");
        }
    }

    private static void ValidateSettings(HandBrakeSettings settings)
    {
        if (settings.Container is not ("mkv" or "mp4"))
        {
            throw new ArgumentException("Container must be mkv or mp4.");
        }
        if (settings.Quality is < 0 or > 51)
        {
            throw new ArgumentException("Quality must be between 0 and 51.");
        }
        if (string.IsNullOrWhiteSpace(settings.VideoEncoder) || string.IsNullOrWhiteSpace(settings.EncoderPreset))
        {
            throw new ArgumentException("Video encoder and encoder preset are required.");
        }
        if (settings.MaxWidth is <= 0 || settings.MaxHeight is <= 0)
        {
            throw new ArgumentException("Maximum dimensions, when set, must be positive.");
        }
        if (settings.AudioBitrateKbps is <= 0)
        {
            throw new ArgumentException("Audio bitrate, when set, must be positive.");
        }

        var forbidden = new[] { "-i", "--input", "-o", "--output", "--queue-import-file", "--preset-import-file", "--preset-import-gui" };
        if (settings.ExtraArguments.Any(argument => IsForbiddenArgument(argument, forbidden)))
        {
            throw new ArgumentException("Extra arguments may not replace input, output, queue, or preset import handling.");
        }
    }

    private static bool IsForbiddenArgument(string argument, IEnumerable<string> forbidden)
    {
        var trimmed = argument.Trim();
        var optionName = trimmed.Split('=', 2)[0];
        return forbidden.Any(item =>
            string.Equals(optionName, item, StringComparison.OrdinalIgnoreCase)
            || (item is "-i" or "-o"
                && trimmed.StartsWith(item, StringComparison.OrdinalIgnoreCase)
                && trimmed.Length > item.Length));
    }

    private static string[] RequiredCapabilities(HandBrakeSettings settings)
    {
        var encoder = settings.VideoEncoder.ToLowerInvariant();
        return ["handbrake", $"encode:{encoder}"];
    }

    private static T Clone<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        return JsonSerializer.Deserialize<T>(json, JsonOptions) ?? throw new InvalidOperationException("Could not clone coordinator state.");
    }
}

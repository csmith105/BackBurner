using BackBurner.Contracts;
using BackBurner.Worker.Core;
using System.Net;
using System.Text.Json;

namespace BackBurner.Tests;

public sealed class WorkerCoreTests : IDisposable
{
    private readonly string temporaryRoot = Path.Combine(Path.GetTempPath(), "backburner-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Logical_paths_resolve_beneath_configured_roots()
    {
        var root = Path.Combine(temporaryRoot, "media");
        var resolver = new LogicalPathResolver(new Dictionary<string, string> { ["incoming"] = root });

        var resolved = resolver.Resolve("incoming:/folder/video.mkv");

        Assert.Equal(Path.Combine(root, "folder", "video.mkv"), resolved);
    }

    [Theory]
    [InlineData("incoming:/../outside.mkv")]
    [InlineData("unknown:/video.mkv")]
    [InlineData("C:\\video.mkv")]
    public void Logical_paths_reject_traversal_and_unknown_roots(string logicalPath)
    {
        var resolver = new LogicalPathResolver(new Dictionary<string, string> { ["incoming"] = temporaryRoot });
        Assert.Throws<InvalidOperationException>(() => resolver.Resolve(logicalPath));
    }

    [Fact]
    public void HandBrake_arguments_are_structured_and_include_job_settings()
    {
        var settings = new HandBrakeSettings
        {
            Container = "mkv",
            VideoEncoder = "x265",
            Quality = 21.5m,
            EncoderPreset = "slow",
            MaxWidth = 1920,
            MaxHeight = 1080,
            ExtraArguments = ["--decomb"]
        };

        var arguments = HandBrakeArgumentBuilder.Build("source with space.mkv", "partial output", settings);

        Assert.Contains("source with space.mkv", arguments);
        Assert.Contains("partial output", arguments);
        Assert.Contains("21.5", arguments);
        Assert.Contains("--decomb", arguments);
        Assert.Equal("--json", arguments[0]);
    }

    [Fact]
    public void Worker_api_key_can_be_supplied_without_writing_it_to_the_configuration_file()
    {
        Directory.CreateDirectory(temporaryRoot);
        var configurationPath = Path.Combine(temporaryRoot, "worker.json");
        File.WriteAllText(configurationPath, JsonSerializer.Serialize(new WorkerConfiguration
        {
            CoordinatorUrl = "http://localhost:5080",
            WorkerId = "environment-key-worker",
            DisplayName = "Environment key worker",
            WorkerApiKey = "file-value",
            Mode = WorkerMode.DedicatedRenderNode,
            Capabilities = ["handbrake"],
            Paths = new Dictionary<string, string> { ["incoming"] = temporaryRoot }
        }));
        var previous = Environment.GetEnvironmentVariable("BACKBURNER_WORKER_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("BACKBURNER_WORKER_API_KEY", "environment-value");

            var loaded = WorkerConfiguration.Load(configurationPath);

            Assert.Equal("environment-value", loaded.WorkerApiKey);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BACKBURNER_WORKER_API_KEY", previous);
        }
    }

    [Fact]
    public void Host_profile_is_available_even_when_the_encoder_probe_will_fail()
    {
        var configuration = CreateDedicatedConfiguration(Path.Combine(temporaryRoot, "inhibits"));

        var profile = ToolProbe.BuildHostProfile(configuration);

        Assert.Equal(Environment.MachineName, profile["hostname"]);
        Assert.False(string.IsNullOrWhiteSpace(profile["os"]));
        Assert.False(string.IsNullOrWhiteSpace(profile["logicalProcessors"]));
    }

    [Fact]
    public void Worker_always_advertises_the_fenced_publication_protocol_once()
    {
        var configuration = CreateDedicatedConfiguration(Path.Combine(temporaryRoot, "inhibits")) with
        {
            Capabilities = ["handbrake", BackBurnerCapabilities.PublicationFenceV1]
        };

        var capabilities = ToolProbe.BuildAdvertisedCapabilities(configuration);

        Assert.Single(capabilities, item => item == BackBurnerCapabilities.PublicationFenceV1);
    }

    [Fact]
    public async Task Empty_worker_api_key_does_not_send_an_authentication_header()
    {
        var handler = new CaptureHandler();
        using var client = new CoordinatorClient(new WorkerConfiguration
        {
            CoordinatorUrl = "http://localhost:5080",
            WorkerId = "lan-worker",
            DisplayName = "LAN worker",
            WorkerApiKey = "",
            Capabilities = ["handbrake"],
            Paths = new Dictionary<string, string> { ["incoming"] = temporaryRoot }
        }, handler);

        await client.HeartbeatAsync(new WorkerHeartbeat
        {
            WorkerId = "lan-worker",
            DisplayName = "LAN worker"
        }, CancellationToken.None);

        Assert.False(handler.HadWorkerKeyHeader);
    }

    [Fact]
    public async Task Publication_authorization_falls_back_to_final_progress_only_for_ordinary_legacy_jobs()
    {
        var handler = new SequenceHandler(HttpStatusCode.NotFound, HttpStatusCode.NoContent);
        using var client = new CoordinatorClient(CreateDedicatedConfiguration(Path.Combine(temporaryRoot, "inhibits")), handler);
        var request = new PublicationAuthorizationRequest
        {
            WorkerId = "test-worker",
            Lease = new LeaseProof(Guid.NewGuid(), 7)
        };

        var accepted = await client.AuthorizePublicationAsync(Guid.NewGuid(), request, requirePublicationFence: false, CancellationToken.None);

        Assert.True(accepted);
        Assert.Collection(
            handler.Paths,
            path => Assert.EndsWith("/authorize-publication", path, StringComparison.Ordinal),
            path => Assert.EndsWith("/progress", path, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Integration_publication_never_falls_back_when_fence_endpoint_is_missing()
    {
        var handler = new SequenceHandler(HttpStatusCode.NotFound);
        using var client = new CoordinatorClient(CreateDedicatedConfiguration(Path.Combine(temporaryRoot, "inhibits")), handler);
        var request = new PublicationAuthorizationRequest
        {
            WorkerId = "test-worker",
            Lease = new LeaseProof(Guid.NewGuid(), 8)
        };

        await Assert.ThrowsAsync<HttpRequestException>(() => client.AuthorizePublicationAsync(
            Guid.NewGuid(),
            request,
            requirePublicationFence: true,
            CancellationToken.None));
        Assert.Single(handler.Paths);
    }

    [Theory]
    [InlineData("Progress: {\"State\":\"WORKING\",\"Working\":{\"Progress\":0.42,\"ETASeconds\":90}}", 0.42, 90)]
    [InlineData("Encoding: task 1 of 1, 25.50 % (30.00 fps, avg 29.00 fps, ETA 0h01m02s)", 0.255, 62)]
    public void HandBrake_progress_parses_json_and_text(string line, decimal expectedFraction, int expectedEta)
    {
        Assert.True(HandBrakeSession.TryParseProgress(line, out var progress));
        Assert.Equal(expectedFraction, progress.Fraction);
        Assert.Equal(expectedEta, progress.EtaSeconds);
    }

    [Fact]
    public void Active_inhibit_marker_blocks_even_a_dedicated_render_node()
    {
        var inhibitDirectory = Path.Combine(temporaryRoot, "inhibits");
        Directory.CreateDirectory(inhibitDirectory);
        File.WriteAllText(Path.Combine(inhibitDirectory, "codex-session.json"), $$"""
            {
              "owner": "Codex test session",
              "reason": "Codex is compiling",
              "expires_at": "{{DateTimeOffset.UtcNow.AddMinutes(10):O}}"
            }
            """);
        var probe = new AvailabilityProbe(CreateDedicatedConfiguration(inhibitDirectory), new WorkerControl());

        var result = probe.Check();

        Assert.Equal(WorkerAvailability.Inhibited, result.Availability);
        Assert.Equal(WorkerBlockingCategory.AgentWork, result.BlockingCategory);
        Assert.Contains("Codex is compiling", result.Reason);
    }

    [Fact]
    public void Expired_inhibit_marker_does_not_block_a_dedicated_render_node()
    {
        var inhibitDirectory = Path.Combine(temporaryRoot, "inhibits");
        Directory.CreateDirectory(inhibitDirectory);
        File.WriteAllText(Path.Combine(inhibitDirectory, "expired.json"), $$"""
            {
              "owner": "old task",
              "reason": "finished",
              "expires_at": "{{DateTimeOffset.UtcNow.AddMinutes(-10):O}}"
            }
            """);
        var probe = new AvailabilityProbe(CreateDedicatedConfiguration(inhibitDirectory), new WorkerControl());

        var result = probe.Check();

        Assert.Equal(WorkerAvailability.Available, result.Availability);
    }

    [Fact]
    public void Shared_worker_accepts_its_own_unexpired_broker_lease_when_development_queue_is_empty()
    {
        var leasePath = Path.Combine(temporaryRoot, "lease.json");
        var queuePath = Path.Combine(temporaryRoot, "queue.json");
        WriteBrokerState(leasePath, queuePath, "backburner-lease", requests: []);
        var configuration = CreateSharedConfiguration(leasePath, queuePath);
        var probe = new AvailabilityProbe(configuration, new WorkerControl());

        var result = probe.Check(jobRunning: true, ownedGameLeaseId: "backburner-lease");

        Assert.Equal(WorkerAvailability.Available, result.Availability);
    }

    [Fact]
    public void Shared_worker_yields_its_broker_lease_when_development_work_queues()
    {
        var leasePath = Path.Combine(temporaryRoot, "lease.json");
        var queuePath = Path.Combine(temporaryRoot, "queue.json");
        WriteBrokerState(leasePath, queuePath, "backburner-lease", requests: [new { request_id = "developer" }]);
        var configuration = CreateSharedConfiguration(leasePath, queuePath);
        var probe = new AvailabilityProbe(configuration, new WorkerControl());

        var result = probe.Check(jobRunning: true, ownedGameLeaseId: "backburner-lease");

        Assert.Equal(WorkerAvailability.GameWorkerReserved, result.Availability);
        Assert.True(result.RequiresImmediateYield);
    }

    [Fact]
    public void Shared_worker_wraps_handbrake_in_the_existing_fenced_broker_scope()
    {
        var configuration = CreateSharedConfiguration("lease.json", "queue.json");
        var broker = new CodyWorkerBroker(configuration);
        var lease = new CodyWorkerLease("lease-id", 42, "request-id", DateTimeOffset.UtcNow.AddMinutes(1), "/tmp/workspace");

        var startInfo = broker.CreateHandBrakeStartInfo(lease, "/usr/bin/HandBrakeCLI", ["--json", "-i", "/media/input.mkv"]);
        var arguments = startInfo.ArgumentList.ToArray();

        Assert.Equal("/usr/local/bin/cody-workerctl", startInfo.FileName);
        Assert.Equal(["--compact", "run", "--lease-id", "lease-id", "--generation", "42", "--cwd", ".", "--", "/usr/bin/HandBrakeCLI", "--json", "-i", "/media/input.mkv"], arguments);
    }

    [Fact]
    public void Shared_worker_acquire_request_is_short_lived_and_never_sticky_in_the_fifo()
    {
        var configuration = CreateSharedConfiguration("lease.json", "queue.json");

        var startInfo = CodyWorkerBroker.CreateAcquireStartInfo(configuration, "request-id", "Test encode");
        var arguments = startInfo.ArgumentList.ToArray();

        Assert.Equal("60", arguments[Array.IndexOf(arguments, "--ttl") + 1]);
        Assert.Equal("60", arguments[Array.IndexOf(arguments, "--queue-ttl") + 1]);
        Assert.Equal("cpu", arguments[Array.IndexOf(arguments, "--profile") + 1]);
    }

    private static WorkerConfiguration CreateDedicatedConfiguration(string inhibitDirectory) => new()
    {
        CoordinatorUrl = "http://localhost:5080",
        WorkerId = "test-worker",
        DisplayName = "Test worker",
        Mode = WorkerMode.DedicatedRenderNode,
        InhibitDirectories = [inhibitDirectory],
        Capabilities = ["handbrake"],
        Paths = new Dictionary<string, string> { ["incoming"] = Path.GetTempPath() }
    };

    private static WorkerConfiguration CreateSharedConfiguration(string leasePath, string queuePath) => new()
    {
        CoordinatorUrl = "http://localhost:5080",
        WorkerId = "shared-worker",
        DisplayName = "Shared worker",
        Mode = WorkerMode.SharedGameWorker,
        GameWorkerLeaseFile = leasePath,
        GameWorkerQueueFile = queuePath,
        Capabilities = ["handbrake"],
        Paths = new Dictionary<string, string> { ["incoming"] = Path.GetTempPath() }
    };

    private static void WriteBrokerState(string leasePath, string queuePath, string leaseId, object[] requests)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(leasePath)!);
        File.WriteAllText(leasePath, JsonSerializer.Serialize(new
        {
            status = "leased",
            generation = 42,
            lease = new
            {
                lease_id = leaseId,
                expires_at = DateTimeOffset.UtcNow.AddMinutes(1)
            }
        }));
        File.WriteAllText(queuePath, JsonSerializer.Serialize(new { requests }));
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public bool HadWorkerKeyHeader { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HadWorkerKeyHeader = request.Headers.Contains("X-BackBurner-Worker-Key");
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NoContent));
        }
    }

    private sealed class SequenceHandler(params HttpStatusCode[] statuses) : HttpMessageHandler
    {
        private readonly Queue<HttpStatusCode> remaining = new(statuses);
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri?.AbsolutePath ?? "");
            var status = remaining.Count > 0 ? remaining.Dequeue() : HttpStatusCode.NoContent;
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true);
    }
}

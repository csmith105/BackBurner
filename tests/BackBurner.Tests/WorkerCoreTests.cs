using BackBurner.Contracts;
using BackBurner.Worker.Core;

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

    public void Dispose()
    {
        if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true);
    }
}

using BackBurner.Contracts;
using BackBurner.Server;
using Microsoft.Extensions.Options;

namespace BackBurner.Tests;

public sealed class SourceBrowserTests : IDisposable
{
    private readonly string temporaryRoot = Path.Combine(Path.GetTempPath(), "backburner-browser-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Scan_returns_only_supported_media_and_honors_recursion()
    {
        Directory.CreateDirectory(Path.Combine(temporaryRoot, "Season 01", "Extras"));
        File.WriteAllText(Path.Combine(temporaryRoot, "Season 01", "Episode 01.mkv"), "video");
        File.WriteAllText(Path.Combine(temporaryRoot, "Season 01", "notes.txt"), "not video");
        File.WriteAllText(Path.Combine(temporaryRoot, "Season 01", "Extras", "Featurette.MP4"), "video");
        var browser = CreateBrowser(maximum: 100);

        var direct = browser.Scan(new DirectoryScanRequest("nas-media:/Season 01"));
        var recursive = browser.Scan(new DirectoryScanRequest("nas-media:/Season 01", Recursive: true));

        Assert.Equal("nas-media:/Season 01", direct.DirectoryPath);
        Assert.Equal("Episode 01.mkv", direct.Files.Single().RelativePath);
        Assert.Equal(2, recursive.Files.Count);
        Assert.Contains(recursive.Files, item => item.LogicalPath == "nas-media:/Season 01/Extras/Featurette.MP4");
        Assert.DoesNotContain(recursive.Files, item => item.FileName == "notes.txt");
    }

    [Fact]
    public void Scan_rejects_traversal_and_unknown_roots()
    {
        Directory.CreateDirectory(temporaryRoot);
        var browser = CreateBrowser(maximum: 100);

        Assert.Throws<ArgumentException>(() => browser.Scan(new DirectoryScanRequest("nas-media:/../outside")));
        Assert.Throws<ArgumentException>(() => browser.Scan(new DirectoryScanRequest("unknown:/Season 01")));
    }

    [Fact]
    public void Scan_reports_when_the_result_limit_is_reached()
    {
        Directory.CreateDirectory(temporaryRoot);
        File.WriteAllText(Path.Combine(temporaryRoot, "one.mkv"), "video");
        File.WriteAllText(Path.Combine(temporaryRoot, "two.mp4"), "video");
        var browser = CreateBrowser(maximum: 1);

        var result = browser.Scan(new DirectoryScanRequest("nas-media:/"));

        Assert.Single(result.Files);
        Assert.True(result.Truncated);
    }

    private SourceBrowser CreateBrowser(int maximum) => new(Options.Create(new CoordinatorOptions
    {
        SourceRoots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["nas-media"] = temporaryRoot },
        MaximumScanFiles = maximum
    }));

    public void Dispose()
    {
        if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true);
    }
}

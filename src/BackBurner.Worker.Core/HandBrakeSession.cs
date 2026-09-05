using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using BackBurner.Contracts;

namespace BackBurner.Worker.Core;

public sealed record EncodeProgress(decimal Fraction, int? EtaSeconds);
public sealed record EncodeResult(int ExitCode, string? ErrorSummary);

public sealed partial class HandBrakeSession : IAsyncDisposable
{
    private readonly Process process;
    private readonly Task<EncodeResult> completion;
    private readonly object progressLock = new();
    private EncodeProgress progress = new(0, null);
    private string? lastErrorLine;
    private int paused;
    private int stopRequested;

    private HandBrakeSession(Process process)
    {
        this.process = process;
        completion = ObserveAsync();
    }

    public Task<EncodeResult> Completion => completion;
    public bool IsPaused => Volatile.Read(ref paused) == 1;
    public EncodeProgress Progress { get { lock (progressLock) return progress; } }

    public static HandBrakeSession Start(
        string executable,
        string source,
        string partialDestination,
        HandBrakeSettings settings)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in HandBrakeArgumentBuilder.Build(source, partialDestination, settings))
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("HandBrakeCLI did not start.");
        }
        return new HandBrakeSession(process);
    }

    public async Task PauseAsync()
    {
        if (process.HasExited || Interlocked.Exchange(ref paused, 1) == 1)
        {
            return;
        }
        await process.StandardInput.WriteLineAsync("p");
        await process.StandardInput.FlushAsync();
    }

    public async Task ResumeAsync()
    {
        if (process.HasExited || Interlocked.Exchange(ref paused, 0) == 0)
        {
            return;
        }
        await process.StandardInput.WriteLineAsync("r");
        await process.StandardInput.FlushAsync();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (process.HasExited || Interlocked.Exchange(ref stopRequested, 1) == 1)
        {
            return;
        }
        try
        {
            await process.StandardInput.WriteLineAsync("q");
            await process.StandardInput.FlushAsync();
            using var grace = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            grace.CancelAfter(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(grace.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between checks.
        }
    }

    private async Task<EncodeResult> ObserveAsync()
    {
        var stdout = ReadLinesAsync(process.StandardOutput, isError: false);
        var stderr = ReadLinesAsync(process.StandardError, isError: true);
        await Task.WhenAll(stdout, stderr, process.WaitForExitAsync());
        return new EncodeResult(process.ExitCode, process.ExitCode == 0 ? null : lastErrorLine ?? "HandBrakeCLI exited unsuccessfully.");
    }

    private async Task ReadLinesAsync(StreamReader reader, bool isError)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            if (isError && !string.IsNullOrWhiteSpace(line))
            {
                lastErrorLine = line.Length <= 500 ? line : line[^500..];
            }
            if (TryParseProgress(line, out var parsed))
            {
                lock (progressLock) progress = parsed;
            }
        }
    }

    public static bool TryParseProgress(string line, out EncodeProgress parsed)
    {
        parsed = new EncodeProgress(0, null);
        var jsonStart = line.IndexOf('{');
        if (jsonStart >= 0)
        {
            try
            {
                using var document = JsonDocument.Parse(line[jsonStart..]);
                decimal? fraction = null;
                int? eta = null;
                Walk(document.RootElement, ref fraction, ref eta);
                if (fraction is not null)
                {
                    parsed = new EncodeProgress(Math.Clamp(fraction.Value, 0, 1), eta);
                    return true;
                }
            }
            catch (JsonException)
            {
                // Some HandBrake versions interleave non-JSON diagnostics; try the text format.
            }
        }

        var match = TextProgressRegex().Match(line);
        if (!match.Success || !decimal.TryParse(match.Groups[1].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var percent))
        {
            return false;
        }
        parsed = new EncodeProgress(Math.Clamp(percent / 100m, 0, 1), ParseTextEta(match.Groups[2].Value));
        return true;
    }

    private static void Walk(JsonElement element, ref decimal? progress, ref int? eta)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals("Progress") && property.Value.TryGetDecimal(out var value)) progress = value;
                if (property.NameEquals("ETASeconds") && property.Value.TryGetInt32(out var seconds)) eta = seconds;
                Walk(property.Value, ref progress, ref eta);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray()) Walk(child, ref progress, ref eta);
        }
    }

    private static int? ParseTextEta(string value)
    {
        var parts = value.Split(['h', 'm', 's'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 3 && parts.All(part => int.TryParse(part.Trim(), out _))
            ? int.Parse(parts[0], CultureInfo.InvariantCulture) * 3600 + int.Parse(parts[1], CultureInfo.InvariantCulture) * 60 + int.Parse(parts[2], CultureInfo.InvariantCulture)
            : null;
    }

    [GeneratedRegex(@"(\d+(?:\.\d+)?)\s*%.*?ETA\s+([0-9]+h[0-9]+m[0-9]+s)", RegexOptions.IgnoreCase)]
    private static partial Regex TextProgressRegex();

    public async ValueTask DisposeAsync()
    {
        if (!process.HasExited)
        {
            await StopAsync(CancellationToken.None);
        }
        process.Dispose();
    }
}

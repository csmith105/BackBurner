using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BackBurner.Worker.Core;

public static class ToolProbe
{
    public static Dictionary<string, string> BuildHostProfile(WorkerConfiguration configuration)
    {
        return new Dictionary<string, string>(configuration.Profile, StringComparer.OrdinalIgnoreCase)
        {
            ["hostname"] = Environment.MachineName,
            ["os"] = RuntimeInformation.OSDescription,
            ["architecture"] = RuntimeInformation.OSArchitecture.ToString(),
            ["logicalProcessors"] = Environment.ProcessorCount.ToString(),
            ["runtime"] = RuntimeInformation.FrameworkDescription
        };
    }

    public static async Task<Dictionary<string, string>> BuildProfileAsync(
        WorkerConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var profile = BuildHostProfile(configuration);
        profile["handBrakeVersion"] = await ReadFirstLineAsync(configuration.HandBrakePath, ["--version"], cancellationToken);
        return profile;
    }

    private static async Task<string> ReadFirstLineAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start '{executable}'.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var stdout = await process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = await process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"'{executable} --version' exited {process.ExitCode}: {stderr.Trim()}");
        }
        return stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
            ?? stderr.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
            ?? "unknown";
    }
}

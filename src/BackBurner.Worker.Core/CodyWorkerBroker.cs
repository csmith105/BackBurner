using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using BackBurner.Contracts;

namespace BackBurner.Worker.Core;

public sealed record CodyWorkerLease(
    string LeaseId,
    long Generation,
    string RequestId,
    DateTimeOffset ExpiresAt,
    string Workspace);

public sealed class CodyWorkerBroker
{
    private readonly WorkerConfiguration configuration;
    private readonly Action<string> log;

    public CodyWorkerBroker(WorkerConfiguration configuration, Action<string>? log = null)
    {
        this.configuration = configuration;
        this.log = log ?? Console.WriteLine;
    }

    public async Task<CodyWorkerLease?> TryAcquireAsync(JobRecord job, CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid().ToString();
        var acquired = false;
        try
        {
            var result = await RunJsonAsync(CreateAcquireStartInfo(configuration, requestId, job.DisplayName), cancellationToken);
            if (!string.Equals(ReadString(result, "result"), "acquired", StringComparison.OrdinalIgnoreCase)
                || !result.TryGetProperty("lease", out var lease))
            {
                log($"Cody worker broker did not grant an immediate background lease for '{job.DisplayName}'.");
                return null;
            }

            CodyWorkerLease brokerLease;
            try
            {
                brokerLease = new CodyWorkerLease(
                    RequiredString(lease, "lease_id"),
                    RequiredInt64(lease, "generation"),
                    RequiredString(lease, "request_id"),
                    RequiredDateTimeOffset(lease, "expires_at"),
                    RequiredString(lease, "workspace"));
            }
            catch (JsonException)
            {
                await ReleaseGrantedLeaseBestEffortAsync(lease);
                throw;
            }
            acquired = true;
            return brokerLease;
        }
        finally
        {
            // An unsuccessful acquire still inserts a queue entry. Background work
            // never waits in or retains priority in the development FIFO.
            if (!acquired)
            {
                await CancelBestEffortAsync(requestId);
            }
        }
    }

    public async Task<CodyWorkerLease> RenewAsync(CodyWorkerLease lease, CancellationToken cancellationToken)
    {
        var startInfo = CreateBrokerStartInfo(configuration.CodyWorkerBrokerPath,
        [
            "--compact", "renew",
            "--lease-id", lease.LeaseId,
            "--generation", lease.Generation.ToString(),
            "--ttl", configuration.CodyWorkerLeaseTtlSeconds.ToString()
        ]);
        var result = await RunJsonAsync(startInfo, cancellationToken);
        if (!string.Equals(ReadString(result, "result"), "renewed", StringComparison.OrdinalIgnoreCase)
            || !result.TryGetProperty("lease", out var renewed))
        {
            throw new InvalidOperationException("Cody worker broker did not renew the BackBurner lease.");
        }
        return lease with { ExpiresAt = RequiredDateTimeOffset(renewed, "expires_at") };
    }

    public async Task ReleaseAsync(CodyWorkerLease lease, CancellationToken cancellationToken)
    {
        var startInfo = CreateBrokerStartInfo(configuration.CodyWorkerBrokerPath,
        [
            "--compact", "release",
            "--lease-id", lease.LeaseId,
            "--generation", lease.Generation.ToString(),
            "--outcome", "complete"
        ]);
        var result = await RunJsonAsync(startInfo, cancellationToken);
        if (!string.Equals(ReadString(result, "result"), "released", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Cody worker broker release ended in '{ReadString(result, "result") ?? "unknown"}'.");
        }
    }

    public ProcessStartInfo CreateHandBrakeStartInfo(
        CodyWorkerLease lease,
        string handBrakeExecutable,
        IReadOnlyList<string> handBrakeArguments)
    {
        return CreateBrokerStartInfo(configuration.CodyWorkerBrokerPath,
        [
            "--compact", "run",
            "--lease-id", lease.LeaseId,
            "--generation", lease.Generation.ToString(),
            "--cwd", ".",
            "--",
            handBrakeExecutable,
            .. handBrakeArguments
        ]);
    }

    public static ProcessStartInfo CreateAcquireStartInfo(
        WorkerConfiguration configuration,
        string requestId,
        string jobName)
    {
        return CreateBrokerStartInfo(configuration.CodyWorkerBrokerPath,
        [
            "--compact", "acquire",
            "--request-id", requestId,
            "--owner", $"BackBurner:{configuration.WorkerId}",
            "--purpose", $"Background media encode: {jobName}",
            "--profile", configuration.CodyWorkerProfile,
            "--ttl", configuration.CodyWorkerLeaseTtlSeconds.ToString(),
            "--queue-ttl", "60"
        ]);
    }

    private async Task CancelBestEffortAsync(string requestId)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await RunJsonAsync(
                CreateBrokerStartInfo(configuration.CodyWorkerBrokerPath, ["--compact", "cancel", "--request-id", requestId]),
                timeout.Token);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or JsonException or OperationCanceledException)
        {
            log($"WARNING: could not cancel Cody worker background request {requestId}: {exception.Message}");
        }
    }

    private async Task ReleaseGrantedLeaseBestEffortAsync(JsonElement lease)
    {
        if (!lease.TryGetProperty("lease_id", out var leaseIdValue)
            || !lease.TryGetProperty("generation", out var generationValue)
            || leaseIdValue.GetString() is not { } leaseId
            || !generationValue.TryGetInt64(out var generation))
        {
            log("CRITICAL: the broker granted a lease with an unreadable fence; manual broker inspection is required.");
            return;
        }
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await RunJsonAsync(
                CreateBrokerStartInfo(configuration.CodyWorkerBrokerPath,
                    ["--compact", "release", "--lease-id", leaseId, "--generation", generation.ToString(), "--outcome", "failed"]),
                timeout.Token);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or JsonException or OperationCanceledException)
        {
            log($"CRITICAL: malformed granted lease {leaseId} could not be released: {exception.Message}");
        }
    }

    private static ProcessStartInfo CreateBrokerStartInfo(string executable, IReadOnlyList<string> arguments)
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
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        return startInfo;
    }

    private static async Task<JsonElement> RunJsonAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"Broker executable '{startInfo.FileName}' did not start.");
            }
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException($"Broker executable '{startInfo.FileName}' could not start: {exception.Message}", exception);
        }
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            throw;
        }
        var output = await stdout;
        var error = await stderr;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Cody worker broker exited {process.ExitCode}: {ShortError(error, output)}");
        }
        using var document = JsonDocument.Parse(output);
        return document.RootElement.Clone();
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) ? value.GetString() : null;

    private static string RequiredString(JsonElement element, string propertyName) =>
        ReadString(element, propertyName) ?? throw new JsonException($"Broker response omitted '{propertyName}'.");

    private static long RequiredInt64(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.TryGetInt64(out var result)
            ? result
            : throw new JsonException($"Broker response omitted '{propertyName}'.");

    private static DateTimeOffset RequiredDateTimeOffset(JsonElement element, string propertyName) =>
        DateTimeOffset.TryParse(RequiredString(element, propertyName), out var result)
            ? result
            : throw new JsonException($"Broker response contained an invalid '{propertyName}'.");

    private static string ShortError(string stderr, string stdout)
    {
        var value = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
        value = value.Trim();
        return value.Length <= 500 ? value : value[^500..];
    }
}

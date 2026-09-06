using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BackBurner.Contracts;

namespace BackBurner.Worker.Core;

public sealed class CoordinatorClient : IDisposable
{
    private readonly HttpClient http;
    private readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public CoordinatorClient(WorkerConfiguration configuration, HttpMessageHandler? handler = null)
    {
        http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        http.BaseAddress = new Uri(configuration.CoordinatorUrl.TrimEnd('/') + "/");
        if (!string.IsNullOrWhiteSpace(configuration.WorkerApiKey))
        {
            http.DefaultRequestHeaders.Add("X-BackBurner-Worker-Key", configuration.WorkerApiKey);
        }
        http.Timeout = TimeSpan.FromSeconds(15);
    }

    public async Task HeartbeatAsync(WorkerHeartbeat heartbeat, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync("api/worker/heartbeat", heartbeat, jsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<JobLease?> ClaimAsync(string workerId, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync("api/worker/claim", new ClaimRequest(workerId), jsonOptions, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return null;
        }
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JobLease>(jsonOptions, cancellationToken);
    }

    public Task<bool> ProgressAsync(Guid jobId, ProgressReport report, CancellationToken cancellationToken) =>
        PostLeaseMessageAsync($"api/worker/jobs/{jobId}/progress", report, cancellationToken);

    public Task<bool> CompleteAsync(Guid jobId, CompletionReport report, CancellationToken cancellationToken) =>
        PostLeaseMessageAsync($"api/worker/jobs/{jobId}/complete", report, cancellationToken);

    public async Task<bool> AuthorizePublicationAsync(
        Guid jobId,
        PublicationAuthorizationRequest request,
        bool requirePublicationFence,
        CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync(
            $"api/worker/jobs/{jobId}/authorize-publication",
            request,
            jsonOptions,
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return false;
        }
        if (!requirePublicationFence && response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
        {
            return await ProgressAsync(jobId, new ProgressReport
            {
                WorkerId = request.WorkerId,
                Lease = request.Lease,
                Progress = 1,
                EtaSeconds = 0
            }, cancellationToken);
        }
        response.EnsureSuccessStatusCode();
        return true;
    }

    public Task<bool> FailAsync(Guid jobId, FailureReport report, CancellationToken cancellationToken) =>
        PostLeaseMessageAsync($"api/worker/jobs/{jobId}/fail", report, cancellationToken);

    public Task<bool> InterruptAsync(Guid jobId, InterruptionReport report, CancellationToken cancellationToken) =>
        PostLeaseMessageAsync($"api/worker/jobs/{jobId}/interrupt", report, cancellationToken);

    private async Task<bool> PostLeaseMessageAsync<T>(string path, T body, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync(path, body, jsonOptions, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return false;
        }
        response.EnsureSuccessStatusCode();
        return true;
    }

    public void Dispose() => http.Dispose();
}

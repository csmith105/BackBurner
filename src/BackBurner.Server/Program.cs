using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using BackBurner.Contracts;
using BackBurner.Server;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<CoordinatorOptions>(builder.Configuration.GetSection("BackBurner"));
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton<StateStore>();
builder.Services.AddSingleton<SourceBrowser>();
builder.Services.AddHostedService<LeaseExpiryService>();

var app = builder.Build();
var coordinatorOptions = app.Configuration.GetSection("BackBurner").Get<CoordinatorOptions>() ?? new CoordinatorOptions();
if (coordinatorOptions.RequireAuthentication && !app.Environment.IsDevelopment() &&
    (string.IsNullOrWhiteSpace(coordinatorOptions.AdminApiKey) || string.IsNullOrWhiteSpace(coordinatorOptions.WorkerApiKey)))
{
    throw new InvalidOperationException("Production authentication requires BackBurner:AdminApiKey and BackBurner:WorkerApiKey.");
}

app.UseDefaultFiles();
app.UseStaticFiles();
app.Use(async (context, next) =>
{
    var expected = context.Request.Path.StartsWithSegments("/api/worker")
        ? coordinatorOptions.WorkerApiKey
        : context.Request.Path.StartsWithSegments("/api/admin")
            ? coordinatorOptions.AdminApiKey
            : null;
    if (!coordinatorOptions.RequireAuthentication || expected is null || (string.IsNullOrEmpty(expected) && app.Environment.IsDevelopment()))
    {
        await next();
        return;
    }

    var headerName = context.Request.Path.StartsWithSegments("/api/worker")
        ? "X-BackBurner-Worker-Key"
        : "X-BackBurner-Admin-Key";
    var actual = context.Request.Headers[headerName].ToString();
    if (!SecretEquals(actual, expected))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new ApiError("Unauthorized."));
        return;
    }
    await next();
});

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", at = DateTimeOffset.UtcNow }));
app.MapGet("/api/config", () => Results.Ok(new
{
    requiresAuthentication = coordinatorOptions.RequireAuthentication && !string.IsNullOrWhiteSpace(coordinatorOptions.AdminApiKey)
}));

var admin = app.MapGroup("/api/admin");
admin.MapGet("/snapshot", (StateStore store, CancellationToken token) => store.SnapshotAsync(token));
admin.MapPost("/presets", async (CreatePresetRequest request, StateStore store, CancellationToken token) =>
{
    try
    {
        return Results.Ok(await store.UpsertPresetAsync(request, token));
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new ApiError(exception.Message));
    }
});
admin.MapDelete("/presets/{id:guid}", async (Guid id, StateStore store, CancellationToken token) =>
    await store.DeletePresetAsync(id, token) ? Results.NoContent() : Results.NotFound());
admin.MapPost("/jobs", async (CreateJobRequest request, StateStore store, CancellationToken token) =>
{
    try
    {
        return Results.Ok(await store.EnqueueAsync(request, token));
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new ApiError(exception.Message));
    }
});
admin.MapPost("/batches", async (CreateBatchRequest request, StateStore store, CancellationToken token) =>
{
    try
    {
        return Results.Ok(await store.EnqueueBatchAsync(request, token));
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new ApiError(exception.Message));
    }
});
admin.MapPost("/source/scan", (DirectoryScanRequest request, SourceBrowser browser) =>
{
    try
    {
        return Results.Ok(browser.Scan(request));
    }
    catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
    {
        return Results.BadRequest(new ApiError(exception.Message));
    }
});
admin.MapPost("/jobs/{id:guid}/retry", async (Guid id, bool resetFailureCount, StateStore store, CancellationToken token) =>
    await store.RetryFailedAsync(id, resetFailureCount, token) ? Results.NoContent() : Results.NotFound());

var worker = app.MapGroup("/api/worker");
worker.MapPost("/heartbeat", async (WorkerHeartbeat heartbeat, StateStore store, CancellationToken token) =>
{
    try
    {
        await store.HeartbeatAsync(heartbeat, token);
        return Results.NoContent();
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new ApiError(exception.Message));
    }
});
worker.MapPost("/claim", async (ClaimRequest request, StateStore store, CancellationToken token) =>
{
    var lease = await store.ClaimAsync(request.WorkerId, token);
    return lease is null ? Results.NoContent() : Results.Ok(lease);
});
worker.MapPost("/jobs/{id:guid}/progress", async (Guid id, ProgressReport report, StateStore store, CancellationToken token) =>
    await store.ProgressAsync(id, report, token) ? Results.NoContent() : Results.Conflict(new ApiError("Stale or invalid lease.")));
worker.MapPost("/jobs/{id:guid}/complete", async (Guid id, CompletionReport report, StateStore store, CancellationToken token) =>
    await store.CompleteAsync(id, report, token) ? Results.NoContent() : Results.Conflict(new ApiError("Stale or invalid lease.")));
worker.MapPost("/jobs/{id:guid}/fail", async (Guid id, FailureReport report, StateStore store, CancellationToken token) =>
    await store.FailAsync(id, report, token) ? Results.NoContent() : Results.Conflict(new ApiError("Stale or invalid lease.")));
worker.MapPost("/jobs/{id:guid}/interrupt", async (Guid id, InterruptionReport report, StateStore store, CancellationToken token) =>
    await store.InterruptAsync(id, report, token) ? Results.NoContent() : Results.Conflict(new ApiError("Stale or invalid lease.")));

app.MapFallbackToFile("index.html");
app.Run();

static bool SecretEquals(string actual, string expected)
{
    var actualBytes = Encoding.UTF8.GetBytes(actual);
    var expectedBytes = Encoding.UTF8.GetBytes(expected);
    return actualBytes.Length == expectedBytes.Length && CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes);
}

public partial class Program;

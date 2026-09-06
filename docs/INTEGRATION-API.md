# Integration API v1

BackBurner exposes a stable JSON API for another application to create an
encoding job, poll it, cancel it, and read a bounded fleet/queue summary. The
OpenAPI 3.1 description is served by every coordinator at:

```text
GET /api/v1/openapi.json
```

The checked-in source is `src/BackBurner.Server/wwwroot/openapi-v1.json`.

## Authorization model

Job creation and the general system summary use the existing administrator API
policy. When coordinator authentication is enabled, send:

```text
X-BackBurner-Admin-Key: <admin key>
```

When the coordinator is deliberately configured for unauthenticated trusted-LAN
operation, that header is omitted.

Creating an integration job returns a random `controlToken` exactly once. The
token is a capability for only that job: it can read or cancel the job, but
cannot create work, inspect other controlled jobs, change presets, or administer
workers. Send it in a header so it does not leak through URL logs:

```text
X-BackBurner-Job-Token: <control token>
```

The coordinator persists only a SHA-256 hash of the token. The caller must keep
the returned token; it cannot be recovered later. A missing or incorrect token
returns the same `404` as an unknown or non-integration job.

## Endpoints

| Method | Path | Purpose | Credential |
| --- | --- | --- | --- |
| `POST` | `/api/v1/jobs` | Create one integration-controlled job | Admin key when enabled |
| `GET` | `/api/v1/jobs/{jobId}` | Poll one created job | Job control token |
| `DELETE` | `/api/v1/jobs/{jobId}` | Cancel one created job | Job control token |
| `GET` | `/api/v1/status?queueLimit=100` | Read counts, queue, workers, and available capabilities | Admin key when enabled |
| `GET` | `/api/v1/openapi.json` | Read the OpenAPI document | None |

All enums are serialized as strings and timestamps use ISO 8601 UTC offsets.
Responses that contain live status send `Cache-Control: no-store`.

## Create a job

Paths are logical paths, never machine-local UNC paths or Linux mounts. Settings
are copied into the job when it is queued, so later preset changes cannot alter
it.

```http
POST /api/v1/jobs HTTP/1.1
Content-Type: application/json

{
  "displayName": "Example encode",
  "sourcePath": "incoming:/synthetic/example-source.mkv",
  "destinationPath": "plex-movies:/Example (2026)/Example (2026).mkv",
  "presetName": "Automation preset",
  "settings": {
    "container": "mkv",
    "videoEncoder": "x265",
    "quality": 22,
    "encoderPreset": "medium",
    "audioEncoder": "copy",
    "allAudio": true,
    "allSubtitles": true,
    "includeChapterMarkers": true,
    "extraArguments": []
  },
  "maxAttempts": 3,
  "clientName": "example-application"
}
```

A successful request returns `201 Created`, a `Location` header, and:

```json
{
  "apiVersion": "v1",
  "jobId": "00000000-0000-0000-0000-000000000000",
  "controlToken": "returned-only-once-placeholder",
  "statusPath": "/api/v1/jobs/00000000-0000-0000-0000-000000000000",
  "cancelPath": "/api/v1/jobs/00000000-0000-0000-0000-000000000000",
  "job": {
    "status": "Queued",
    "progress": 0,
    "failureCount": 0,
    "maxAttempts": 3,
    "interruptionCount": 0,
    "cancellationAllowed": true,
    "isTerminal": false
  }
}
```

The abbreviated nested object above illustrates the important fields; use the
OpenAPI schema as the complete contract. Creation is not currently idempotent.
If a client times out without receiving the response, it must not blindly retry
unless it is prepared to reconcile or cancel a possible duplicate.

## Poll a job

```http
GET /api/v1/jobs/{jobId} HTTP/1.1
X-BackBurner-Job-Token: <control token>
```

Possible `status` values are `Queued`, `Leased`, `Running`, `Paused`,
`Succeeded`, `Failed`, and `Canceled`. `failureCount` advances only for encoder
failures. `interruptionCount` includes a cancellation of active work but does
not consume the retry budget. `isTerminal` is true for succeeded, failed, or
canceled work.

Poll every 2–10 seconds for an interactive client; faster polling has no benefit
because workers normally report on their configured poll interval.

## Cancel a job

```http
DELETE /api/v1/jobs/{jobId} HTTP/1.1
X-BackBurner-Job-Token: <control token>
```

Cancellation is idempotent after a successful cancellation: repeating it returns the same
`Canceled` status. A queued job becomes terminal immediately and cannot be
claimed. An active job is marked `Canceled`, its lease is invalidated, and its
worker stops and deletes only the partial output when the next fenced heartbeat
or progress update is rejected. No encoding failure is charged. The worker may
therefore appear busy for one short poll interval after the API response.

Publication and cancellation are serialized by a separate worker fencing call.
If cancellation wins, publication authorization is rejected. If publication
authorization wins, `cancellationAllowed` becomes false and cancellation returns
`409 Conflict`; the worker may finish the atomic rename and report success. A
job that already succeeded or failed also returns `409` because the API never
deletes published media or erases failure history.

“Remove” therefore means remove from scheduling and cancel execution, not erase
the audit record. This preserves event and attempt history.

## Read queue and worker status

```http
GET /api/v1/status?queueLimit=100 HTTP/1.1
```

The response contains:

- job counts for every state;
- total, currently available, working, and offline worker counts;
- `availableCapabilities`, mapping each capability to the number of idle,
  available workers advertising it;
- the oldest queued jobs, bounded by `queueLimit` from 1 through 500;
- every registered worker's role, availability, blocker, readiness time,
  heartbeat time, capabilities, and active-job summary.

This is intentionally an operational summary, not a full health, metrics, log,
or administration API. It omits media paths and HandBrake settings.

## Client storage rules

- Store `jobId` and `controlToken` together in the calling application's secret
  or private state.
- Never place a control token in a URL, query string, log message, exception,
  analytics event, or Git repository.
- Do not infer success from progress reaching 100%; wait for `Succeeded`.
- Treat `Failed` and `Canceled` as terminal. A failed integration job cannot be
  retried through v1; create a new job after correcting the cause.
- Treat `409` from cancellation as a signal to poll the job again.

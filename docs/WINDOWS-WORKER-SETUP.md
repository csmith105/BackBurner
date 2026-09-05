# Windows worker setup

This is the handoff for an operator—or a Codex task the operator starts—to add a
Windows machine to an existing BackBurner coordinator. The public repository contains
no household hostnames, share paths, credentials, or media names. Get the
coordinator URL and the correct logical-to-local path map privately from the
coordinator owner.

## What the installer does

The worker is a native Windows notification-area application built from this
repository. The installer publishes a self-contained executable under the
current user's `%LOCALAPPDATA%`, writes its machine-local configuration outside
the checkout, adds a per-user Startup shortcut, and launches it. The worker
pulls jobs from the coordinator; Windows does not need an inbound firewall rule.

A `PersonalDesktop` waits for 15 minutes of human and machine inactivity before
claiming work. When a human returns, it drains by finishing the current video by
default and exposes pause and Stop & Requeue controls from its tray icon. A
`DedicatedRenderNode` is immediately eligible and should be selected only for a
machine whose normal purpose is batch work.

## Prerequisites

1. Windows 10 or 11 on x64 hardware.
2. Git and PowerShell 7 (`pwsh`).
3. The .NET 10 SDK. The worker is self-contained after publishing, so the SDK
   is needed for installation or upgrade, not every launch.
4. The Windows HandBrakeCLI archive from the official HandBrake site, extracted
   to a stable local directory. Confirm `HandBrakeCLI.exe --version` works.
5. OS-level access to every required SMB share. Store SMB credentials in
   Windows Credential Manager, never in this repository or worker JSON.

## Install

Open PowerShell 7, clone the public repository, and enter it:

```powershell
git clone https://github.com/csmith105/BackBurner.git
Set-Location .\BackBurner
```

Have the coordinator owner provide values for the placeholders, then run this
from PowerShell 7. Every worker needs a stable, unique `WorkerId`. Use
`PersonalDesktop` on an everyday PC or `DedicatedRenderNode` on a
Windows machine reserved for encoding.

```powershell
$paths = @{
    'nas-media' = '\\YOUR-NAS\YOUR-SOURCE-SHARE'
    'plex-movies' = '\\YOUR-NAS\YOUR-MOVIE-DESTINATION'
    'plex-series' = '\\YOUR-NAS\YOUR-SERIES-DESTINATION'
}

& .\deploy\windows\install-worker.ps1 `
    -CoordinatorUrl 'http://YOUR-COORDINATOR:5080' `
    -WorkerId 'unique-machine-id' `
    -DisplayName 'Friendly machine name' `
    -Mode PersonalDesktop `
    -HandBrakePath 'C:\Tools\HandBrakeCLI\HandBrakeCLI.exe' `
    -PathMappings $paths
```

The default capability list is deliberately conservative: HandBrake plus CPU
x264 and x265. Do not add NVENC, QSV, VCN, or upscaling capabilities just
because the machine has a GPU. Verify the tool and driver on that machine first,
then reinstall with an explicit `-Capabilities` list.

The current trusted-LAN deployment may run without authentication. In that
case no key is needed. If authentication is enabled, leave `workerApiKey` empty
in the generated JSON and set `BACKBURNER_WORKER_API_KEY` in the worker user's
environment through a private channel. Never pass a key on the installer command
line or commit it.

## Verify without touching media

1. Confirm a BackBurner icon appears in the Windows notification area. It may
   be under the overflow arrow.
2. Open the coordinator dashboard and find the new worker under **Workers &
   queue**. Confirm its heartbeat, role, hardware profile, and capabilities.
3. On a personal desktop, keep using the mouse for this first check. The worker
   should show normal human activity and must not claim a job.
4. Use the tray menu's **Pause new jobs** before changing paths or capabilities.
5. Do not queue a production job as an installation test. A first encode test
   must use synthetic media and temporary non-library roots approved for that
   test; never write into an existing Plex library during validation.

The generated configuration is
`%LOCALAPPDATA%\BackBurner\Worker\worker.local.json`. It is machine state, not
source code. To replace it, exit the running tray worker and rerun the installer
with `-ReplaceConfiguration`. A new Git revision installs beside the old release
so a broken upgrade does not erase the previous binary.

## Codex exclusion

If Codex also runs on the machine, follow `CODEX-INTEGRATION.md`. The worker's
CPU/activity fallback helps, but the per-session inhibit marker is the reliable
signal. An open Codex window alone is not an inhibit; an active task is.

## Copy/paste prompt for the operator's Codex

```text
Clone https://github.com/csmith105/BackBurner.git and set up this Windows PC as
a BackBurner worker. Before changing or running anything, read AGENTS.md,
README.md, docs/ARCHITECTURE.md, docs/WORKER-CONTRACT.md, docs/DECISIONS.md,
docs/OPERATIONS.md, and docs/WINDOWS-WORKER-SETUP.md in full. Ask me privately
for the coordinator URL, the worker's display name/mode, the HandBrakeCLI path,
and the logical path mappings; do not guess them. Never commit worker.local.json,
credentials, real media names, or machine-local paths. Run the Windows installer,
verify HandBrakeCLI, start the notification-area worker, and confirm only that
its heartbeat appears correctly on the dashboard. Keep new jobs paused and do
not scan, queue, rename, move, or encode real media during setup.
```

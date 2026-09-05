# Windows worker deployment

`install-worker.ps1` publishes a self-contained `win-x64` notification-area
worker into the current user's `%LOCALAPPDATA%\BackBurner\Worker` directory. It
writes an untracked machine-local configuration, starts the worker, and creates
a Startup shortcut by default. Administrator rights are not required.

Read `../../docs/WINDOWS-WORKER-SETUP.md` before running it. The installer will
not accept an unreachable path mapping, advertise capabilities not explicitly
listed, stop a running worker, or collect an API key on the command line.

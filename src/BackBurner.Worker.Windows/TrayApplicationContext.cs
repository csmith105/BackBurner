using BackBurner.Worker.Core;

namespace BackBurner.Worker.Windows;

public sealed class TrayApplicationContext : ApplicationContext, IWorkerNotifier
{
    private readonly CancellationTokenSource shutdown = new();
    private readonly WorkerControl control = new();
    private readonly WorkerRuntimeStatus runtimeStatus = new();
    private readonly NotifyIcon trayIcon;
    private readonly ToolStripMenuItem statusItem;
    private readonly ToolStripMenuItem pauseNewJobsItem;
    private readonly Control dispatcher = new();
    private readonly WorkerAgent agent;
    private HumanReturnForm? humanReturnForm;
    private readonly Task agentTask;

    public TrayApplicationContext(WorkerConfiguration configuration)
    {
        dispatcher.CreateControl();
        statusItem = new ToolStripMenuItem("Starting…") { Enabled = false };
        pauseNewJobsItem = new ToolStripMenuItem("Pause new jobs") { CheckOnClick = true };
        pauseNewJobsItem.CheckedChanged += (_, _) => control.SetOperatorPaused(pauseNewJobsItem.Checked);

        var pauseCurrent = new ToolStripMenuItem("Pause current encode", null, (_, _) => control.RequestPause());
        var resumeCurrent = new ToolStripMenuItem("Resume current encode", null, (_, _) => control.RequestResume());
        var stopCurrent = new ToolStripMenuItem("Stop current && requeue", null, (_, _) => control.RequestStopAndRequeue());
        var exit = new ToolStripMenuItem("Exit", null, (_, _) => ExitThread());
        var menu = new ContextMenuStrip();
        menu.Items.AddRange([statusItem, new ToolStripSeparator(), pauseNewJobsItem, pauseCurrent, resumeCurrent, stopCurrent, new ToolStripSeparator(), exit]);

        trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "BackBurner — starting",
            ContextMenuStrip = menu,
            Visible = true
        };
        trayIcon.DoubleClick += (_, _) => ShowCurrentStatus();
        runtimeStatus.Changed += OnStatusChanged;

        agent = new WorkerAgent(configuration, control, runtimeStatus, this, message => System.Diagnostics.Debug.WriteLine(message));
        agentTask = Task.Run(() => agent.RunAsync(shutdown.Token));
        _ = agentTask.ContinueWith(task =>
        {
            if (task.Exception is not null)
            {
                dispatcher.BeginInvoke(() => MessageBox.Show(
                    task.Exception.GetBaseException().Message,
                    "BackBurner worker stopped",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error));
            }
        }, TaskScheduler.Default);
    }

    public Task HumanReturnedAsync(WorkerStatusSnapshot status, CancellationToken cancellationToken)
    {
        dispatcher.BeginInvoke(() =>
        {
            trayIcon.ShowBalloonTip(
                8_000,
                "BackBurner is wrapping up one video",
                $"{status.JobName}\n{FormatEta(status.EtaSeconds)}. Open BackBurner to pause or stop and requeue.",
                ToolTipIcon.Info);
            if (humanReturnForm is null || humanReturnForm.IsDisposed)
            {
                humanReturnForm = new HumanReturnForm(control, runtimeStatus);
            }
            humanReturnForm.UpdateStatus(status);
            humanReturnForm.ShowNearNotificationArea();
        });
        return Task.CompletedTask;
    }

    public Task InformationAsync(string title, string message, CancellationToken cancellationToken)
    {
        dispatcher.BeginInvoke(() => trayIcon.ShowBalloonTip(5_000, title, message, ToolTipIcon.Info));
        return Task.CompletedTask;
    }

    private void OnStatusChanged(WorkerStatusSnapshot status)
    {
        if (dispatcher.IsDisposed) return;
        dispatcher.BeginInvoke(() =>
        {
            var text = status.JobName is null
                ? $"{status.Availability}: {status.Reason}"
                : $"{status.JobName} — {status.Progress:P0}";
            statusItem.Text = text.Length > 70 ? text[..67] + "…" : text;
            trayIcon.Text = text.Length > 63 ? text[..60] + "…" : text;
            humanReturnForm?.UpdateStatus(status);
        });
    }

    private void ShowCurrentStatus()
    {
        var current = runtimeStatus.Current;
        if (current.JobName is null)
        {
            trayIcon.ShowBalloonTip(4_000, "BackBurner", $"{current.Availability}: {current.Reason}", ToolTipIcon.Info);
            return;
        }
        humanReturnForm ??= new HumanReturnForm(control, runtimeStatus);
        humanReturnForm.UpdateStatus(current);
        humanReturnForm.ShowNearNotificationArea();
    }

    protected override void ExitThreadCore()
    {
        shutdown.Cancel();
        trayIcon.Visible = false;
        humanReturnForm?.Close();
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            shutdown.Cancel();
            trayIcon.Dispose();
            dispatcher.Dispose();
            agent.Dispose();
            shutdown.Dispose();
        }
        base.Dispose(disposing);
    }

    private static string FormatEta(int? seconds)
    {
        if (seconds is null) return "Finish time is not known yet";
        var remaining = TimeSpan.FromSeconds(seconds.Value);
        return remaining.TotalHours >= 1
            ? $"About {(int)remaining.TotalHours}h {remaining.Minutes}m remaining"
            : $"About {Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes))} minutes remaining";
    }
}

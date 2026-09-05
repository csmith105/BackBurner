using BackBurner.Worker.Core;

namespace BackBurner.Worker.Windows;

public sealed class HumanReturnForm : Form
{
    private readonly WorkerControl control;
    private readonly Label title;
    private readonly Label detail;
    private readonly ProgressBar progress;

    public HumanReturnForm(WorkerControl control, WorkerRuntimeStatus runtimeStatus)
    {
        this.control = control;
        Text = "BackBurner";
        Width = 440;
        Height = 205;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.FromArgb(23, 34, 30);
        ForeColor = Color.FromArgb(236, 242, 238);
        Font = new Font("Segoe UI", 9.5f);

        title = new Label { AutoSize = false, Left = 18, Top = 17, Width = 392, Height = 28, Font = new Font(Font, FontStyle.Bold), Text = "BackBurner is wrapping up one video" };
        detail = new Label { AutoSize = false, Left = 18, Top = 49, Width = 392, Height = 42 };
        progress = new ProgressBar { Left = 18, Top = 94, Width = 392, Height = 12, Maximum = 1000 };
        var pause = new Button { Text = "Pause until idle", Left = 18, Top = 123, Width = 125, Height = 32 };
        var requeue = new Button { Text = "Stop && requeue", Left = 151, Top = 123, Width = 125, Height = 32 };
        var keepRunning = new Button { Text = "Keep running", Left = 284, Top = 123, Width = 126, Height = 32 };
        pause.Click += (_, _) => { control.RequestPause(); Hide(); };
        requeue.Click += (_, _) => { control.RequestStopAndRequeue(); Hide(); };
        keepRunning.Click += (_, _) => Hide();
        Controls.AddRange([title, detail, progress, pause, requeue, keepRunning]);

        FormClosing += (_, eventArgs) =>
        {
            if (eventArgs.CloseReason == CloseReason.UserClosing)
            {
                eventArgs.Cancel = true;
                Hide();
            }
        };
        runtimeStatus.Changed += status =>
        {
            if (!IsDisposed && IsHandleCreated) BeginInvoke(() => UpdateStatus(status));
        };
    }

    public void UpdateStatus(WorkerStatusSnapshot status)
    {
        if (IsDisposed) return;
        title.Text = status.IsPaused ? "BackBurner paused the current video" : "BackBurner is wrapping up one video";
        detail.Text = status.JobName is null
            ? status.Reason
            : $"{status.JobName}\n{FormatEta(status.EtaSeconds)}";
        progress.Value = Math.Clamp((int)(status.Progress * 1000), 0, 1000);
    }

    public void ShowNearNotificationArea()
    {
        var working = Screen.PrimaryScreen?.WorkingArea ?? Screen.GetWorkingArea(Cursor.Position);
        Location = new Point(working.Right - Width - 14, working.Bottom - Height - 14);
        if (!Visible) Show();
        BringToFront();
    }

    private static string FormatEta(int? seconds)
    {
        if (seconds is null) return "Estimating finish time…";
        var remaining = TimeSpan.FromSeconds(seconds.Value);
        return remaining.TotalHours >= 1
            ? $"About {(int)remaining.TotalHours}h {remaining.Minutes}m remaining"
            : $"About {Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes))} minutes remaining";
    }
}

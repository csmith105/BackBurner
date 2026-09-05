using System.Diagnostics;

namespace BackBurner.Worker.Core;

public sealed record ProcessActivitySnapshot(decimal SystemCpuPercent, decimal CodexCpuPercent, bool IsPrimed);

public sealed class ProcessActivityMonitor
{
    private readonly object sync = new();
    private Dictionary<int, ProcessSample> previous = [];
    private DateTimeOffset previousAt;

    public ProcessActivitySnapshot Sample()
    {
        lock (sync)
        {
            var now = DateTimeOffset.UtcNow;
            var current = new Dictionary<int, ProcessSample>();
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        current[process.Id] = new ProcessSample(process.TotalProcessorTime, IsCodexProcess(process.ProcessName));
                    }
                    catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        // The process exited or cannot be inspected; it contributes no sample.
                    }
                }
            }

            if (previousAt == default)
            {
                previous = current;
                previousAt = now;
                return new(0, 0, false);
            }

            var elapsed = now - previousAt;
            double systemMilliseconds = 0;
            double codexMilliseconds = 0;
            foreach (var (id, sample) in current)
            {
                if (!previous.TryGetValue(id, out var old) || sample.Cpu < old.Cpu) continue;
                var delta = (sample.Cpu - old.Cpu).TotalMilliseconds;
                systemMilliseconds += delta;
                if (sample.IsCodex || old.IsCodex) codexMilliseconds += delta;
            }
            previous = current;
            previousAt = now;
            if (elapsed <= TimeSpan.Zero)
            {
                return new(0, 0, false);
            }
            var capacityMilliseconds = elapsed.TotalMilliseconds * Math.Max(1, Environment.ProcessorCount);
            return new(
                (decimal)Math.Clamp(systemMilliseconds / capacityMilliseconds * 100, 0, 100),
                (decimal)Math.Clamp(codexMilliseconds / capacityMilliseconds * 100, 0, 100),
                true);
        }
    }

    private static bool IsCodexProcess(string processName)
    {
        return processName.Equals("codex", StringComparison.OrdinalIgnoreCase) ||
               processName.StartsWith("codex-", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ProcessSample(TimeSpan Cpu, bool IsCodex);
}

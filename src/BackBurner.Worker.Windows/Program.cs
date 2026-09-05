using BackBurner.Worker.Core;

namespace BackBurner.Worker.Windows;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Length != 1)
        {
            MessageBox.Show(
                "Start BackBurner with the path to worker.local.json as its one argument.",
                "BackBurner configuration required",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        try
        {
            Application.Run(new TrayApplicationContext(WorkerConfiguration.Load(args[0])));
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "BackBurner could not start", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

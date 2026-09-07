using Oeps.KicadProductionFiles.Core.Configuration;
using Oeps.KicadProductionFiles.Core.Updates;

namespace Oeps.KicadProductionFiles.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        try
        {
            var dataIndex = Array.IndexOf(args, "--data-dir");
            if (dataIndex >= 0 && (dataIndex + 1 == args.Length || args[dataIndex + 1].StartsWith("--")))
                throw new ArgumentException("--data-dir requires a directory path.");
            var paths = new AppPaths(dataIndex >= 0 ? args[dataIndex + 1] : null);
            var config = AppConfiguration.Load(AppContext.BaseDirectory, paths.UserDataRoot);
            if (args.Contains("--sample")) config.SampleMode = true;
            if (args.Contains("--ui-smoke") && (!config.SampleMode || dataIndex < 0))
                throw new ArgumentException("UI smoke requires --sample and an isolated --data-dir.");
            MainForm? form = null;
            // Sample-only smoke verification runs separately from the user's open application.
            using var instance = args.Contains("--ui-smoke") ? null : AppInstanceCoordinator.TryAcquire(() =>
            {
                if (form is not { IsHandleCreated: true, IsDisposed: false }) return;
                try
                {
                    form.BeginInvoke(() =>
                    {
                        if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
                        form.Show(); form.Activate();
                    });
                }
                catch (InvalidOperationException) { }
            });
            if (instance is null && !args.Contains("--ui-smoke")) { AppInstanceCoordinator.SignalReadyFromArguments(args); return; }
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(40) };
            using (form = new MainForm(config, paths, http, args)) Application.Run(form);
        }
        catch (Exception ex)
        {
            Environment.ExitCode = 1;
            MessageBox.Show($"OEPS KiCad Production Files could not start.\n\n{ex.Message}",
                "Startup error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
